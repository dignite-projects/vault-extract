# Per-Document-Type Permissions

By default, what a user may do with documents is an all-or-nothing decision: the module-wide permissions in `VaultExtractPermissions` either admit every document of the layer or none.

This page describes the narrower grants: **this user (or role) may read / edit / delete / upload into these document types and no others** — and the axis that sits beside them: **whoever uploaded a document may view, edit and delete it, whatever type it landed on.**

They are built on ABP's [resource-based authorization](https://abp.io/docs/latest/framework/fundamentals/authorization), not on anything Vault Extract invented — the grants, the management API, the user / role lookup and the dialog are all ABP's.

## What exists

| Kind | Name | Meaning |
| --- | --- | --- |
| Standard permission | `VaultExtract.Documents` | **Entry.** May enter the documents area and work inside their own type scope. It is also the parent of every permission below it, and the SPA route gate. |
| Standard permission | `VaultExtract.Documents.ReadAll` | May read **every** type of the caller's layer, plus untyped documents. |
| Standard permission | `VaultExtract.DocumentTypes.ManagePermissions` | May open the per-type permission dialog and grant / revoke the grants below. |
| Resource permission | `…DocumentType.Upload` | May upload a document declaring **one specific** document type. |
| Resource permission | `…DocumentType.Read` | May read **all** documents of **one specific** type — other people's included. |
| Resource permission | `…DocumentType.Edit` | May run the operator edit family, and resolve reviews, on **all** documents of **one specific** type. |
| Resource permission | `…DocumentType.Delete` | May soft-delete **all** documents of **one specific** type, and restore them again. |

The four resource permission names are prefixed with the resource name `Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType`. The resource is the `DocumentType` entity; the resource **key** is its immutable `Id`.

`ManagePermissions` is deliberately **not** `DocumentTypes.Update`; the rationale is recorded in [#629](https://github.com/dignite-projects/vault-extract/issues/629).

Every one of those strings is a **frozen contract** once the first grant row exists — the same discipline the `Extract:*` error codes follow. They are persisted verbatim in `AbpResourcePermissionGrants`; renaming any one of them orphans every existing grant silently. In particular the resource name must stay equal to `typeof(DocumentType).FullName`, because ABP derives it from the runtime type of the object handed to the authorization service. A unit test asserts that equality so an entity rename fails the build instead of the ACL.

## `Documents` is entry, `Documents.ReadAll` is read

`VaultExtract.Documents` used to mean both "may enter the documents area" and "may read every document". Those are now two names.

The split is what makes a per-type `Read` grant expressible at all. `Documents` was the gate on every read endpoint and on every SPA documents route, so **every principal that can open the documents area holds it and therefore sees every document of the layer** — a per-type `Read` hung off that alone would never narrow anyone.

ABP documents `PermissionDefinition.Parent` as "this permission can be granted only if the parent is granted", and the permission-management dialog enforces it by checking the parent with the child — but `PermissionChecker` does **not** consult `Parent` at check time, so a permission granted programmatically through `IPermissionManager` can exist without its parent. That nuance does not weaken the paragraph above, which rests on the route guard and the read gates rather than on the parent rule.

> **Breaking change.** A role holding `VaultExtract.Documents` without `ReadAll` now sees only the types it holds a `Read` grant on. The seeded `DocumentManager` and `Viewer` roles gain `ReadAll` automatically on the next migration run (`VaultExtractHostRoleDataSeedContributor` applies it idempotently), and `admin` receives it from the migrator's permission seed. **Hand-made roles and MCP OAuth clients need a manual `ReadAll` grant** or their document lists go empty.

## The rule every enforcement point applies

**An operation on a document is authorized by `VaultExtract.Documents` (entry) AND either the module-wide permission for that operation, OR the caller owns the document and the rule allows it, OR the matching grant on the document's current type.** Within the OR, the module-wide permission remains sufficient on its own; the other two are the narrower alternatives.

Put the other way round, which is the sentence to remember:

> **Whoever uploaded a document may view, edit and delete it. The per-type `Read` / `Edit` / `Delete` grants are what extend those rights to *other people's* documents of that type.**

### Entry gates every operation, without exception

Entry is a precondition of the whole rule, not only of the read endpoints, and it gates all three arms: a principal who may not open the documents area may not mutate documents in it either. Every principal assembled through ABP's permission dialog carries it, because each of these module-wide permissions is a child of `VaultExtract.Documents` and the dialog grants the parent with the child; only a programmatic `IPermissionManager` grant or a hand-edited store can separate them, since `PermissionChecker` never consults `Parent` at check time.

It matters most for the per-type arm: handing out a resource grant is gated by `DocumentTypes.ManagePermissions`, which says nothing about `Documents.*`, so without this precondition granting "Delete on Invoices" to a principal holding no `Documents` permission at all would produce a caller that could soft-delete, restore and rewrite the Markdown of a document it could not read.

**Every operation in the table below is on the table for the same reason** — including the ones with no per-type arm. They used to declare their gate with an `[Authorize]` attribute instead, which meant `Documents.PermanentDelete`, `Documents.Pipelines.Retry`, `Documents.Reprocessing.*` and `Documents.Export` each reached documents without entry ever being asserted.

### The rule table

| Rule | Module-wide | Per-type grant | Owner may | Operations it gates |
| --- | --- | --- | --- | --- |
| **Read** | `Documents.ReadAll` | `Read` | **yes** | `GetAsync`, `GetBlobAsync`, list and recycle-bin membership, export rows, `DocumentPipelineRunAppService.GetListAsync`, MCP `get_document` / `search_documents` / document resources |
| **Edit** | `Documents.ConfirmClassification` | `Edit` | **yes** | `ConfirmClassificationAsync` / `ReclassifyAsync` (whose **target** type is judged by *Declare a type*), `RerecognizeAsync`, `ReextractFieldsAsync`, `UpdateExtractedFieldsAsync`, `UpdateMarkdownAsync`, `UpdateCabinetAsync` |
| **Review** | `Documents.ConfirmClassification` | `Edit` | **no** | `AllowDuplicateAsync`, `ResolveFieldValidationWarningsAsync`, `RejectReviewAsync` |
| **Delete** | `Documents.Delete` | `Delete` | **yes** | `DeleteAsync` (soft delete) |
| **Restore** | `Documents.Restore` | `Delete` — **whoever may delete may undo** | **yes** | `RestoreAsync` |
| **Retry** | `Documents.Pipelines.Retry` | `Edit` | **yes** | `RetryPipelineAsync` |
| **Declare a type** | `Documents.ConfirmClassification` | `Upload` on the **target** type | no | `UploadAsync`'s `DocumentTypeId`, and the target type of `ConfirmClassificationAsync` / `ReclassifyAsync` |
| **Upload** | `Documents.Upload` | — | no | `UploadAsync` admission, checked before *Declare a type* |
| **Permanent delete** | `Documents.PermanentDelete` | — | no | `PermanentDeleteAsync` |
| **Reprocess (fields)** | `Documents.Reprocessing.FieldExtraction` | — | no | `PreviewFieldExtractionAsync`, `StartFieldExtractionAsync` |
| **Reprocess (classification)** | `Documents.Reprocessing.Reclassification` | — | no | `PreviewReclassificationAsync`, `StartReclassificationAsync` |
| **Export** | `Documents.Export` | — | no | `ExportAsync` admission; the rows in the file are narrowed by the **Read** scope |
| **Statistics** | `Documents.ReadAll` | — | no | the overview statistics, which is also where the list page's review-count badge comes from |

Reclassifying a document from type A to type B needs two rows: **Edit** on A (it is an edit of that document) and **Declare a type** on B (it is a decision about B), or the module-wide permission in place of either. Owning the document satisfies the first and never the second — owning a document is not a licence to move it into a type you were never granted.

**Review is why ownership is a per-rule flag rather than one global arm.** Its two permission names are identical to Edit's; the *only* difference is that the ownership arm is shut. Those three methods clear a blocking review reason — the channel's data-quality gate on the way to `DocumentReadyEto` — and a suspected duplicate or a field-validation warning exists precisely so that someone other than the uploader checks the uploader's work. A duplicate invoice is the adversarial case the review queue is for. (Editing field values on one's own document does **not** clear a validation warning: only `ResolveFieldValidationWarningsAsync` removes one, so Review is not reachable through the back door.)

**Restore** is the one row whose per-type arm is not its own resource permission: it reuses `Delete`. Undoing an operation is not a wider right than the operation, and a per-type deleter who could not restore would have to escalate a mistake of their own making to an admin.

**Retry** used to be module-wide only, by decision. It is not any more: it is a single-document operator action on the detail page, the same act as `RerecognizeAsync` beside it, and none of the reasons the remaining module-wide-only rows have (irreversible, admin-level bulk, whole-layer aggregate) applies to it. Left as it was, a caller holding a `Read` grant plus `Pipelines.Retry` re-ran OCR and classification on any readable document, around the per-type `Edit` gate.

**`UpdateCabinetAsync`** moved from Read to Edit: it calls `SetCabinet` + `UpdateAsync`, so filing a document is a write however it is described, and leaving it on Read meant a `Read` grant was no longer read-only. Assigning to a cabinet still additionally requires `Cabinets.Default`.

Stays module-wide only, by decision: permanent delete (irreversible, and it destroys the blob a restorable sub-document reaches through its provenance pointer), everything under `Reprocessing.*` (admin-level bulk over a whole type), and the overview statistics (a whole-layer aggregate has no per-type — or per-uploader — meaning). Cabinet reads, field-definition reads and `GetVisibleAsync` keep their existing gates, in which `Documents` reads as entry.

### What "own" means

**`Document.CreatorId`**, ABP's audit property, set at insert from `ICurrentUser.Id`. Nothing new is persisted and there is no migration.

- **Derived sub-documents inherit the origin's owner.** Segmentation runs in a background job with no principal, so ABP would leave `CreatorId` null and the sub-documents of a bundle would be invisible to the person who uploaded the bundle. `Document.CreateDerived` takes the origin's `CreatorId`; ABP's audit setter returns early when `CreatorId` already has a value, so the explicit value survives.
- **Machine identities have no owner.** A client-credentials token has no `sub`, so `ICurrentUser.Id` is null and the ownership arm can never match. Such a principal reaches documents only through module-wide permissions or per-type grants. Today's MCP client is Authorization Code + PKCE, so real MCP callers are users and do carry ownership.
- **Not transferable, and there is no UI for it.** Out of scope.
- **Revoking `Upload` does not retract ownership.** Nothing short of removing entry stops an uploader from editing or soft-deleting their own document, including one that has already fired `DocumentReadyEto`. Approval workflows belong downstream, and downstream already handles `DocumentDeletedEto`.

### Untyped documents

A document with no `DocumentTypeId` — unclassified, failed classification, or a container — belongs to no type, so **no grant can ever cover it**. Two arms reach it: the module-wide permission, and **its uploader**. That second arm is the point: before it, the documents that most need a human — the ones whose classification failed — were exactly the ones their uploader could not reach.

Sub-documents carry their own type and are covered by it, plus the owner they inherit from their origin.

### Existence is validated before permission

Every lookup runs under ABP's ambient `IMultiTenant` filter, so a cross-layer id resolves to nothing and fails with `EntityNotFoundException` before the permission layer is reached. That is why a grant on a Host-layer type id cannot authorize a tenant caller, and why a probing caller learns nothing from the error about which types exist in another layer.

## Where the rule lives in the code

- **The table is data.** `DocumentAccessRule` is a record of three fields — module-wide permission, optional per-type grant, and whether the owner may perform it — with one static member per row above. A call site reads `CheckAsync(DocumentAccessRule.Edit, subject)` and cannot pair "edit" with the read permission.
- **One helper, `DocumentAccessChecker`**, evaluates every row, in two shapes and no more: `IsGrantedAsync` / `CheckAsync` answer "may this caller do this to this one subject", and `ResolveScopeAsync` answers "what may this caller reach at all". **Entry is asserted in exactly one place**, reached by both, so there is no way to ask the checker a question and get a permissive answer for a caller who may not open the documents area.
- **The subject is `DocumentAccessSubject(DocumentTypeId, CreatorId)`**, built from a loaded document, from a target type alone (no owner), or from nothing at all for an operation that reads neither fact.
- **The checks are programmatic, not `[Authorize]` attributes.** MCP and reflection dispatch paths do not run attributes, and an attribute fires before the method body — so it would deny a per-type grant holder, or an owner, before the body could offer the other arms of the OR. No method of `DocumentAppService`, `DocumentExportAppService` or `DocumentReprocessingAppService` carries one, and neither does any of those classes.
- **The scope is one object, `DocumentAccessScope`**, non-nullable, and the single source for the list predicate, the export's row narrowing, the recycle bin's rows and the per-row rights. `Unrestricted` is the module-wide holder; every other scope carries "the types I was granted OR the documents I uploaded" as an expression, and a scope that reaches nothing carries its own always-false predicate rather than making each caller branch on emptiness. It reaches the query through `DocumentQueries.ApplyMetadataFilter`, the single chain the operator list, the export and the MCP search all pass through, so the list's total count is narrowed by the same predicate as its rows.
- **Grants are resolved once per request.** A scoped `DocumentTypeGrantMap` reads the layer's types once (traversing soft delete, so a document on an archived type stays visible to a caller granted on it) and asks ABP's multi-name `IResourcePermissionChecker` overload once per type, for all four grants together. The checker, the scope and the per-row rights all read that one map, so a list, its rights column and the detail page behind it cannot disagree and a request costs one grant check per type however many questions it asks. `IResourcePermissionStore.GetGrantedResourceKeysAsync` is deliberately not used: it filters on resource + permission name only and is not per-user, so it would report every type that carries a grant for anyone.
- **Rights are decided on the server.** `DocumentListItemDto` and `DocumentDto` carry a `rights` object (`canRead` / `canEdit` / `canReview` / `canDelete` / `canRestore` / `canRetry`) computed by the same checker; the client binds actions to it instead of re-deriving the rule. No `creatorId` is exposed — the client needs to know what it may do with a document, never who owns it.
- **Background jobs are not affected.** They run without a principal and are not user-facing reads.

### Two orderings worth knowing

- **The recycle bin is admitted by entry alone**, and its rows are the Read scope's soft-deleted documents, own included. It used to admit by the Restore arm and narrow by the Read arm — two different questions — so a caller holding only a `Delete` grant was let into a bin the UI then reported as empty while it was not.
- **`GetListAsync` resolves the scope first.** A requested `DocumentTypeCode` the caller can produce no row of returns an **empty page**, not a 403, and returns it before any field filter is resolved — otherwise the unknown-field error would answer "type X has no field named Y" to a caller who cannot see a single document of X. An empty page rather than a refusal because an owner legitimately lists a type they hold no grant on and gets their own rows back; for that caller an unknown field name also yields an empty page instead of the correctable error, since describing a type's schema to someone holding no grant on it discloses more than their rows do.

### Existence, and what an export contains

`ExportAsync` admits on the **Export** row and narrows the rows in the file by the **Read** scope — the same predicate the screen runs, so "download the current view" keeps meaning the view. It no longer refuses a type the caller holds no `Read` grant on: after ownership that test could not distinguish "you may see nothing of this type" from "you may see your own", so it would have had to admit every caller with an owner arm, which is every real user. An unknown type **code** still loud-fails: "this type does not exist in your layer" is a statement about the layer, not about the caller.

## Upload: declaring a type

A caller's **type scope** for upload is:

- **every type of the caller's own layer** if the caller holds `Documents.ConfirmClassification`;
- otherwise, **the types the caller holds an `Upload` resource grant on**, directly or through a role.

| Request | Requirement |
| --- | --- |
| `DocumentTypeId` supplied | The id must resolve to a type in the caller's own layer, **then** `ConfirmClassification` **or** the `Upload` grant on that type. Otherwise `AbpAuthorizationException`, with no blob written and no document inserted. |
| `DocumentTypeId` omitted (untyped) | `ConfirmClassification`. |

A resource-granted declaration keeps the same semantics as any other declared type: classification confidence `1.0`, `ReviewDisposition = Confirmed`, no classification LLM call. The grant is precisely the delegation of that decision, for that one type. See [classification](../pipeline/classification.md).

### Untyped upload requires `ConfirmClassification`

This was a behaviour change in the release that introduced `Upload` grants. An `Upload`-only caller used to be able to upload without a type and let the pipeline classify.

Keeping that would make the per-type grant trivially bypassable: upload untyped, let the LLM classify the document into a type the caller was never granted, and the document still reaches the downstream consumers that subscribe by `(TenantId, DocumentTypeCode)`.

**Migration:** any role that holds `Documents.Upload` without `Documents.ConfirmClassification` and relies on untyped upload must be granted `ConfirmClassification`. The seeded `DocumentManager` role already carries it, applied idempotently on the next migration run — roles created by hand still need the manual grant.

## Picking a type in the UI

`IDocumentTypeAppService.GetVisibleAsync` admits `Documents` (entry), `DocumentTypes.Default` **or** `Documents.Upload` holders — an upload-only caller has to see the list to pick from it.

On `GetVisibleAsync`, every returned `DocumentTypeDto` carries a `resourcePermissions` dictionary filled with the calling principal's own grants on that type — all four keys — so the client does not have to guess; other endpoints return it empty.

The MCP tools and resources list types for an LLM and never read the dictionary, so they call a separate, narrower method, `GetVisibleSummariesAsync`, which returns `DocumentTypeSummaryDto` — identity and display text only, with no `resourcePermissions` member at all — instead of paying for one multi-permission check per type and discarding the result.

Client-side rights are a convenience only; the server enforces the same rule regardless of what the client sends. Since #635 the client is handed the answer rather than the inputs — see the `rights` object below.

## What the operator UI shows

The lists themselves are narrowed on the server, so the UI never hides a row it was sent. What it gates is the per-row actions, and it gates them from the **`rights` object on the row** — the answer, not the inputs.

| Surface | Shown when |
| --- | --- |
| Document list / detail → Confirm classification, Reclassify, Re-recognize, Re-extract fields, Edit fields, Correct Markdown, Change cabinet | `rights.canEdit` |
| Document list / detail → Delete | `rights.canDelete` |
| Document list → selection checkboxes and bulk delete | `rights.canDelete` on at least one row of the page; rows without it drop out of the selection |
| Document detail → Reject review, Allow duplicate, Resolve warnings | `rights.canReview` |
| Document detail → Retry | `rights.canRetry` (and the run being retryable) |
| Recycle bin → Restore | `rights.canRestore` |
| Confirm / Reclassify → the type picker | lists only the types the caller may **assign**: `ConfirmClassification`, or an `Upload` grant on that type — a question about *types*, so it is still answered from `GetVisibleAsync`'s `resourcePermissions` |
| Document list → **Needs review** toggle, overview → **Needs review** quick link | Edit module-wide, or an `Edit` grant on at least one visible type — after this change the review queue is a reviewer's surface, and an uploader whose own document is pending review sees it in the ordinary list with its status |
| Document list → review-count badge, overview → statistics card | `Documents.ReadAll` (both are whole-layer aggregates) |
| Document list → Export, Upload; overview → recycle bin | `Documents.Export`, `Documents.Upload`, and entry, respectively |

Because the answer comes from the server, an **untyped** document (unclassified, failed classification, container) offers its actions to its uploader, and a document on an **archived** type offers exactly the actions the API still admits — the two cases a client-side copy of the rule got wrong, the second because it keyed types by `TypeCode` against the active types while the server keys by `Id` across soft-deleted ones.

The **overview's statistics card** is hidden without `Documents.ReadAll`, and the request behind it is not made at all: these are whole-layer aggregates and the endpoint requires `ReadAll`, so an entry-only caller would only collect a 403. Routes are unchanged — `Documents` still gates entry to the documents area.

The two rows of the table above that are questions about *types* rather than about a document — the type picker and the **Needs review** affordances — read the visible types from a single root-provided `DocumentTypesStore`, which owns one `GetVisibleAsync` call for the whole session and answers "still loading" and "the fetch failed" for itself. It exists because those are different answers: an empty type list because the request has not returned is not a statement about the caller's grants, and rendering it as one told operators to go ask an administrator for access they already had.

The **recycle bin** asks nothing before it queries. Admission is entry, the rows are the caller's readable soft-deleted documents including their own, and each row carries its `rights` — so it has exactly one empty state, and an empty bin is empty by construction.

## Granting and revoking

There is no Vault Extract API for managing these grants. They go through ABP's standard resource-permission endpoints (`/api/permission-management/permissions/resource…`), gated by `ManagePermissions`, and through ABP's `ResourcePermissionManagementComponent` dialog in the operator UI — which renders one checkbox per definition, so the four grants need no dialog work. User and role lookup comes from `Volo.Abp.PermissionManagement.Domain.Identity`, which the host already references.

In the operator UI, the dialog is reached from **Document Types → row actions → Permissions**, visible only to a caller holding `ManagePermissions`.

Grants are stored in **`AbpResourcePermissionGrants`**, an `IMultiTenant` table that has existed since the `Initial` migration — enabling this feature needs no schema change. A row is unique on `(TenantId, Name, ResourceName, ResourceKey, ProviderName, ProviderKey)` and is distributed-cache backed. The provider is `U` for a direct user grant, `R` for a role grant (and `C` for an OAuth client).

## Grant lifecycle

- **Soft delete and restore of a document type keep its grants.** `DocumentType` has no hard delete today; if one is ever added, it must call `IResourcePermissionManager.DeleteAsync` for the resource name and that id, or the rows are orphaned.
- **A grant on a soft-deleted type still counts.** The read scope is resolved across soft delete, so a document classified to a since-archived type stays visible to a caller holding a `Read` grant on it — the same rows a `ReadAll` holder sees. Archiving a type is a schema decision, not a revocation.
- **Role delete / rename and user delete are cleaned up by ABP**, through the event handlers in `Volo.Abp.PermissionManagement.Domain.Identity`.
- **Renaming a type's `TypeCode` is irrelevant.** The resource key is the immutable `Id`, not the code.

## Not covered

Resource permissions on `Cabinet` or on an individual `Document` are out of scope, and rejected rather than deferred: cabinets are a filing dimension, not a security boundary, and per-document sharing is a different feature with a different resource. Every operation not listed in the table above stays governed by the module-wide permissions in `VaultExtractPermissions`: a caller without the full permission cannot do it at all.
