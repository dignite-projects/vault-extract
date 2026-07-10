---
description: "Dignite Vault Extract sub-document segmentation subsystem: the two-representation model (DocumentSegment ledger + derived Document), the two-Kind red line, the field lifecycle contract, and the cross-entity invariant (decision record: #390)"
paths:
  - "**/Segmentation/**/*.cs"
  - "**/DerivedDocumentSpawner.cs"
  - "**/ContainerMarker*.cs"
  - "**/Segments/**/*.cs"
  - "**/DocumentSegment*.cs"
---

# Sub-document Segmentation

> Auto-loaded when editing the segmentation / sub-document detection subsystem. This is the densest, most-iterated
> subsystem in the channel (#306 → #346 → #355/#356/#359 → #364/#365 → #371/#372/#373 → #377/#379 → #381). Read this
> **before** touching it: the field-level "is this redundant?" questions are almost always answered by the
> two-representation model below, recorded as the resolution of #390.

## Two representations of a sub-document — on purpose

A sub-document exists first as a **`DocumentSegment`** (pre-spawn ledger row) and then as a **derived `Document`**
(post-spawn first-class artifact). This is deliberate denormalization, not accidental duplication:

- **Detection is a single expensive LLM pass.** We need a persistent "detected but not yet spawned" state so the job
  is resumable / idempotent without re-paying the LLM split. A `Document` cannot serve this role — the moment one
  exists, the whole pipeline (parse → classify → extract) takes it over; it cannot sit as a "pending slice".
- **Retraction (#364) needs `Kind` to outlive spawn.** A container→type reclassify retracts `Text` children but keeps
  `Figure` children; that decision reads the ledger row after the derived document already exists.

Therefore `DocumentSegment` is a **durable internal ledger**, **not** a transient work queue:

- It is **never deleted after parse success** (the "delete-segment-after-parse" aggressive simplification is a
  boundary change, explicitly deferred — #390 Decision 4; open an issue if ever pursued).
- It is **never on any egress** (REST / MCP / ETO). Downstream discovers sub-documents only as `Document` rows with
  `OriginDocumentId` set. Verified zero references in `*.Application.Contracts` / `*.Mcp` / `*.Abstractions`. Keep it
  that way — exposing the ledger would make an internal work-queue into a channel contract.

The fields mirrored between the two representations are intentional copies made at spawn time:
`DocumentSegment.SegmentKey → Document.OriginConstituentKey`, `SourceDocumentId → OriginDocumentId`,
`SliceText → Document.Markdown` (one-way seed), `TenantId → TenantId`. A derived sub-document carries **no
`FileOrigin` of its own** — `Document.FileOrigin` is nullable again and a spawned sub-document is created with
`fileOrigin: null`. The markdown-slice split does not give children a source file: **#487 decided against
physically splitting the source** (the cost of a per-child faithful file was not worth it), which reverted #481's
required-`FileOrigin` back to the pre-#481 nullable design, and Phase A of #487 deleted the #477/#478 figure-image
retention chain so figures now stay **inline** in the parent Markdown (no retained blob, no `FigureManifest`). To
reach a sub-document's source, a consumer follows `OriginDocumentId` to the parent and downloads the parent's blob
(a frontend concern). The parse job seeds a derived document's Markdown from its `SliceText` and **never touches a
blob** (its `FileOrigin?.BlobName` is null) — see `Text_Extraction_Seeds_Derived_Document_From_Segment_Slice`.

## 🚦 RED LINE: the subsystem assumes exactly two Kinds {Text, Figure}

`DocumentSegmentKind.IsContainerIndependent()` (exhaustive switch, throws on an unhandled kind), the
`Strip` vs `ExtractBodies` child-seed split, and the **Text-only** retraction filter in
`ContainerMarkerClearedEventHandler` are all **binary**. Adding a third `DocumentSegmentKind`
(Table / Signature / Attachment / …) is a **CHANNEL BOUNDARY change**:

> **STOP and open a GitHub issue** to re-review *every* `IsContainerIndependent()` call site and the
> container→type retraction semantics **before** writing code.

The exhaustive switch throwing at runtime on an unhandled kind is the **backstop** (it prevents a silent wrong
default — the #364-class missed-branch bug, hardened in #379), not a license to add a kind quietly.

## Field lifecycle contract (do not add / remove without updating #390)

| Field | Status | Note |
|---|---|---|
| `SourceDocumentId`, `SegmentKey`, `Kind`, `Status`, `RoutedDocumentId`, `Ordinal` | **contract** | the idempotency + retraction lifecycle |
| `RoutedDocumentId` | **keep explicit** | reconstructable from `(OriginDocumentId, OriginConstituentKey)`, but the explicit pointer is the ledger's purpose (idempotency + retraction clarity); reconstructing the join is more complex, not less |
| `SliceText` | **transient one-way seed** | duplicated by `Document.Markdown` after parse; bounded duplication is accepted; **do not add a parse→segment writeback to clear it** (couples parse job to the ledger for a low-value saving) |

_Removed by #487: `PageNumber` and `FigureContentHash`. Both were **figure-only**, and since #487 deleted the figure-image retention chain (Phase A) figure spans are **detected-but-skipped** — they no longer route to sub-documents — so neither column was ever written again. Dropped from the entity (+ EF config + `DocumentSegmentConsts.MaxFigureContentHashLength`) with the `V487_DropDormantSegmentFigureColumns` migration._

_`Kind = Figure` is **legacy-only** since #487: fresh detection writes only `Text` (a figure span is skipped before
any row is persisted), so `Figure` survives only on rows persisted by pre-#487 deployments. Two retention rules:
a **Spawned** Figure row is deliberately kept — its `SegmentKey` is the **sole duplicate-spawn barrier** (#481
moved spawn idempotency entirely onto this ledger after dropping the Document-side unique index) and its `Kind`
shields its live sub-document from the #364 retraction; a **still-Pending** Figure row is deleted on encounter by
`DocumentSegmentationJob.DeleteLegacyFigureSegmentAsync` instead of spawned (the restored Phase A guard — the #487
FileOrigin revert had dropped it). **Do not delete Spawned Figure rows out-of-band** (e.g. a cleanup migration):
the "live routed child ⟺ its ledger row exists (while the source lives)" invariant is what keeps re-splits
idempotent and the ≥2 bundle count honest. The `Figure` enum value becomes safely removable only when no such rows
remain (they leave via the parent's hard-delete FK cascade)._

Watch for accreted residue: a fast-iterated subsystem leaves write-only fields / never-assigned enum values / dead
projections. #390 removed three (`DocumentSegmentStatus.NotADocument`, `DetectionContext.UploadedByUserName`,
`PendingSegment.SliceText`); #487 removed two more (`PageNumber`, `FigureContentHash`). Re-run a dead-code sweep when
adding the next iteration.

## Cross-entity invariant (two coupled state machines)

`DocumentSegment` (`Status` Pending/Spawned + `Kind`) and `Document` (`IsContainer` / `IsSegmented`) are two coupled
state machines. The coupling rules:

- **All** container↔concrete transitions flow through `Document.SetContainerFlag`, which clears `IsSegmented`
  (#378/#379 — the single choke point so the coupled invariant cannot leak).
- `IsSegmented` is the **precise resume gate** (#377): set atomically with the segment rows on terminal SUCCESS;
  do not infer completion from segment-row `Kind`.
- A non-document slice (cover / index / transmittal) is **skipped, not persisted** — there is no terminal
  "not a document" row state.

Changes to these transitions must be covered by the cross-entity invariant tests (container→type→container round
trips: assert segment-row + sub-document terminal states). When in doubt, this is a boundary — open an issue.
