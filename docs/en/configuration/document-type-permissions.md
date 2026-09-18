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
| Resource permission | `…DocumentType.Upload` | May upload into **one specific** document type — on its own, without `Documents.Upload` — and assign it as a reclassification's target type. |
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

## Configuring the three tiers

Every document permission has three tiers, joined by OR, all behind entry (`VaultExtract.Documents`, "Access Documents"):

1. **Role level — all types.** A module-wide permission, ticked in the ordinary permission grid.
2. **Type level — this type.** A grant on one document type, made from **Document Types → row actions → Permissions**.
3. **Ownership — your own documents.** Nothing to configure: whoever uploaded a document may read, edit and delete it (see the owner arm below).

The first two read as a pair in the two dialogs:

| Operation | Role level (all types) | Type level (this type) |
| --- | --- | --- |
| Read | Read Documents of All Types (`Documents.ReadAll`) | Read All Documents of this Type |
| Edit and review | Edit Documents of All Types (`Documents.ConfirmClassification`) | Edit All Documents of this Type |
| Delete and restore | Delete Documents of All Types (`Documents.Delete`) | Delete All Documents of this Type |
| Upload | Upload into All Document Types (`Documents.Upload`) | Upload into this Document Type |

The permission **names** are frozen contracts, which is why `ConfirmClassification` keeps a name narrower than what it grants; only the display names pair up.

**Example: an ordinary uploader** who may only upload invoices. Grant **Access Documents**, and on the *Invoice* type grant **Upload into this Document Type**. Nothing else. They can upload into *Invoice* and nowhere else; they cannot upload without choosing a type, because the classifier could put the document into a type they were never granted; and through ownership they see, correct (unless it is held for review) and withdraw what they uploaded. Adding a second type is one more grant on that type.

Other shapes follow the same way: a reviewer for one type is entry plus the *Read* and *Edit* grants on it; an operator for every type holds the role-level permissions instead. The seeded `DocumentManager` role is entry plus `ReadAll`, `Upload`, `Export` and `ConfirmClassification`.

## The rule every enforcement point applies

**An operation on a document is authorized by `VaultExtract.Documents` (entry) AND either the module-wide permission for that operation, OR the caller owns the document and the rule allows it, OR the matching grant on the document's current type.** Within the OR, the module-wide permission remains sufficient on its own; the other two are the narrower alternatives. (For one row, *Declare a type*, two module-wide permissions each admit — see the table.)

Put the other way round, which is the sentence to remember:

> **Whoever uploaded a document may view, edit and delete it. The per-type `Read` / `Edit` / `Delete` grants are what extend those rights to *other people's* documents of that type.**

### Entry gates every operation, without exception

Entry is a precondition of the whole rule, not only of the read endpoints, and it gates all three arms: a principal who may not open the documents area may not mutate documents in it either. Every principal assembled through ABP's permission dialog carries it, because each of these module-wide permissions descends from `VaultExtract.Documents` — most as children, `Documents.Pipelines.Retry` and `Documents.Reprocessing.*` as grandchildren via `Documents.Pipelines` / `Documents.Reprocessing` — and the dialog grants the whole ancestor chain, not only the immediate parent, when a permission is ticked. Only a programmatic `IPermissionManager` grant or a hand-edited store can separate them, since `PermissionChecker` never consults `Parent` at check time.

It matters most for the per-type arm: handing out a resource grant is gated by `DocumentTypes.ManagePermissions`, which says nothing about `Documents.*`, so without this precondition granting "Delete on Invoices" to a principal holding no `Documents` permission at all would produce a caller that could soft-delete, restore and rewrite the Markdown of a document it could not read.

**Every operation in the table below is on the table for the same reason** — including the ones with no per-type arm. They used to declare their gate with an `[Authorize]` attribute instead, which meant `Documents.PermanentDelete`, `Documents.Pipelines.Retry`, `Documents.Reprocessing.*` and `Documents.Export` each reached documents without entry ever being asserted.

### The rule table

| Rule | Module-wide | Per-type grant | Owner arm | Operations it gates |
| --- | --- | --- | --- | --- |
| **Read** | `Documents.ReadAll` | `Read` | **always** | `GetAsync`, `GetBlobAsync`, list and recycle-bin membership, export rows, `DocumentPipelineRunAppService.GetListAsync`, MCP `get_document` / `search_documents` / document resources |
| **Edit** | `Documents.ConfirmClassification` | `Edit` | **unless under review** | `ConfirmClassificationAsync` / `ReclassifyAsync` (whose **target** type is judged by *Declare a type*), `RerecognizeAsync`, `ReextractFieldsAsync`, `UpdateExtractedFieldsAsync`, `UpdateMarkdownAsync`, `UpdateCabinetAsync` |
| **Review** | `Documents.ConfirmClassification` | `Edit` | **never** | `AllowDuplicateAsync`, `ResolveFieldValidationWarningsAsync`, `RejectReviewAsync` |
| **Delete** | `Documents.Delete` | `Delete` | **always** | `DeleteAsync` (soft delete) |
| **Restore** | `Documents.Delete` — **whoever may delete may undo** | `Delete` — likewise | **always** | `RestoreAsync` |
| **Retry** | `Documents.Pipelines.Retry` | `Edit` | **unless under review** | `RetryPipelineAsync` |
| **Declare a type** | `Documents.ConfirmClassification` **or** `Documents.Upload` | `Upload` on the **target** type | never | the target type of `ConfirmClassificationAsync` / `ReclassifyAsync` |
| **Upload** | `Documents.Upload` | `Upload` on the declared type | never | `UploadAsync`, judged once; an untyped upload has no type, so only `Documents.Upload` admits it |
| **Permanent delete** | `Documents.PermanentDelete` | — | never | `PermanentDeleteAsync` |
| **Reprocess (fields)** | `Documents.Reprocessing.FieldExtraction` | — | never | `PreviewFieldExtractionAsync`, `StartFieldExtractionAsync` |
| **Reprocess (classification)** | `Documents.Reprocessing.Reclassification` | — | never | `PreviewReclassificationAsync`, `StartReclassificationAsync` |
| **Export** | `Documents.Export` | — | never | `ExportAsync` admission; the rows in the file are narrowed by the **Read** scope |
| **Statistics** | `Documents.ReadAll` | — | never | the overview statistics, which is also where the list page's review-count badge comes from |

The module-wide column of each row is a set, and any member admits. Every row has one member except **Declare a type**: a reviewer holding `ConfirmClassification` assigns any type, and so does anyone holding `Documents.Upload`, who could have uploaded the document into any type in the first place.

Reclassifying a document from type A to type B needs two rows: **Edit** on A (it is an edit of that document) and **Declare a type** on B (it is a decision about B), or a module-wide permission in place of either. Owning the document satisfies the first and never the second — owning a document is not a licence to move it into a type you were never granted.

### The owner arm has three values, and Review is why

**Review** carries exactly the same two permission names as **Edit**; the *only* difference is the owner arm. Its three methods clear a blocking review reason — the channel's data-quality gate on the way to `DocumentReadyEto` — and a suspected duplicate or a field-validation warning exists precisely so that someone other than the uploader checks the uploader's work. A duplicate invoice is the adversarial case the review queue is for.

But **closing those three to an owner is not enough**, and that is why the arm is three-valued rather than a yes/no. The edit family clears the same bits as a *side effect*, without ever asking to:

- `UpdateExtractedFieldsAsync` clears `FieldExtractionIncomplete` outright — that is #491's deliberate escape path, where manual entry *is* the resolution — and an empty field set is enough to trigger it, which releases the document to Ready and fires `DocumentReadyEto`;
- `UpdateMarkdownAsync(reprocess: true)` and `ReextractFieldsAsync` re-run extraction, and re-extraction replaces the **whole** validation-warning set and recomputes the duplicate fingerprint from the new values — so correcting the body clears `FieldValidationWarning` *and* `DuplicateSuspected`;
- `ConfirmClassificationAsync` resets duplicate state and clears warnings, and reclassifying to the **same** type is not refused as a no-op;
- a retried field-extraction run does what re-extraction does.

So the rule lives where it can actually hold:

> **While a document carries a blocking review reason other than `UnresolvedClassification`, an uploader may still read and delete it, but not modify it.** Modification is for someone holding the module-wide permission or the per-type `Edit` grant.

That set — `DuplicateSuspected`, `FieldExtractionIncomplete`, `FieldValidationWarning` — is `ReviewReasonPolicy.OwnerLocking`, **derived** as "every blocking reason except classification" so a blocking reason added later locks owners out by default and has to be excluded on purpose. Classification is the one exclusion: confirming or reclassifying one's own upload is exactly what an uploader is expected to do, and the target type is judged separately by **Declare a type**, which has no owner arm at all.

The client sees this without a special case: `rights.canEdit` and `rights.canRetry` simply come back false for a locked owner, while `canRead` and `canDelete` stay true.

**Restore** is the **Delete** row: at the role level it is `Documents.Delete`, at the type level the `Delete` grant, and the owner may always restore their own. Undoing an operation is not a wider right than the operation, and a deleter who could not restore would have to escalate a mistake of their own making to an admin. There is no separate restore permission any more — see the CHANGELOG entry for #645 if a role of yours held one.

**Retry** used to be module-wide only, by decision. It is not any more: it is a single-document operator action on the detail page, the same act as `RerecognizeAsync` beside it, and none of the reasons the remaining module-wide-only rows have (irreversible, admin-level bulk, whole-layer aggregate) applies to it. Left as it was, a caller holding a `Read` grant plus `Pipelines.Retry` re-ran OCR and classification on any readable document, around the per-type `Edit` gate.

**`UpdateCabinetAsync`** moved from Read to Edit: it calls `SetCabinet` + `UpdateAsync`, so filing a document is a write however it is described, and leaving it on Read meant a `Read` grant was no longer read-only. Assigning to a cabinet still additionally requires `Cabinets.Default`.

Stays module-wide only, by decision: permanent delete (irreversible, and it destroys the blob a restorable sub-document reaches through its provenance pointer), everything under `Reprocessing.*` (admin-level bulk over a whole type), and the overview statistics (a whole-layer aggregate has no per-type — or per-uploader — meaning). Cabinet reads, field-definition reads and `GetVisibleAsync` keep their existing gates, in which `Documents` reads as entry.

### What "own" means

**`Document.CreatorId`**, ABP's audit property, set at insert from `ICurrentUser.Id`. Nothing new is persisted and there is no migration.

- **Derived sub-documents inherit the origin's owner.** Segmentation runs in a background job with no principal, so ABP would leave `CreatorId` null and the sub-documents of a bundle would be invisible to the person who uploaded the bundle. `Document.CreateDerived` takes the origin's `CreatorId`; ABP's audit setter returns early when `CreatorId` already has a value, so the explicit value survives.

  Sub-documents split out **before** the upgrade have no owner (they were spawned with no principal and no inherited creator); the one-off backfill that corrects them is the deployment note in the [CHANGELOG](../../../CHANGELOG.md) entry for #635.
- **Machine identities have no owner.** A client-credentials token has no `sub`, so `ICurrentUser.Id` is null and the ownership arm can never match. Such a principal reaches documents only through module-wide permissions or per-type grants. Today's MCP client is Authorization Code + PKCE, so real MCP callers are users and do carry ownership.
- **Not transferable, and there is no UI for it.** Out of scope.
- **Revoking `Upload` does not retract ownership.** Nothing short of removing entry stops an uploader from editing or soft-deleting their own document, including one that has already fired `DocumentReadyEto`. Approval workflows belong downstream, and downstream already handles `DocumentDeletedEto`.

### Untyped documents

A document with no `DocumentTypeId` — unclassified, failed classification, or a container — belongs to no type, so **no grant can ever cover it**. Two arms reach it: the module-wide permission, and **its uploader**. That second arm is the point: before it, the documents that most need a human — the ones whose classification failed — were exactly the ones their uploader could not reach.

Sub-documents carry their own type and are covered by it, plus the owner they inherit from their origin.

### Existence is validated before permission

Every lookup runs under ABP's ambient `IMultiTenant` filter, so a cross-layer id resolves to nothing and fails with `EntityNotFoundException` before the permission layer is reached. That is why a grant on a Host-layer type id cannot authorize a tenant caller, and why a probing caller learns nothing from the error about which types exist in another layer.

## Where the rule lives in the code

- **The table is data.** `DocumentAccessRule` is a record of three fields — the module-wide permissions (a set; any member admits), an optional per-type grant, and whether the owner may perform it — with one static member per row above. Its equality compares the set by content. A call site reads `CheckAsync(DocumentAccessRule.Edit, subject)` and cannot pair "edit" with the read permission.
- **One helper, `DocumentAccessChecker`**, evaluates every row, in two shapes and no more: `IsGrantedAsync` / `CheckAsync` answer "may this caller do this to this one subject", and `ResolveScopeAsync` answers "what may this caller reach at all". **Entry is asserted in exactly one place**, reached by both, so there is no way to ask the checker a question and get a permissive answer for a caller who may not open the documents area.
- **The subject is `DocumentAccessSubject(DocumentTypeId, CreatorId)`**, built from a loaded document, from a target type alone (no owner), or from nothing at all for an operation that reads neither fact.
- **The checks are programmatic, not `[Authorize]` attributes.** MCP and reflection dispatch paths do not run attributes, and an attribute fires before the method body — so it would deny a per-type grant holder, or an owner, before the body could offer the other arms of the OR. No method of `DocumentAppService`, `DocumentExportAppService` or `DocumentReprocessingAppService` carries one, and neither does any of those classes.
- **The scope is one object, `DocumentAccessScope`**, non-nullable, and the single source for the list predicate, the export's row narrowing, the recycle bin's rows and the per-row rights. `Unrestricted` is the module-wide holder; every other scope carries "the types I was granted OR the documents I uploaded" as an expression, and a scope that reaches nothing carries its own always-false predicate rather than making each caller branch on emptiness. It reaches the query through `DocumentQueries.ApplyMetadataFilter`, the single chain the operator list, the export and the MCP search all pass through, so the list's total count is narrowed by the same predicate as its rows.
- **Grants are resolved once per request.** A scoped `DocumentAccessMemo` reads the layer's types once (traversing soft delete, so a document on an archived type stays visible to a caller granted on it) and asks ABP's multi-name `IResourcePermissionChecker` overload once per type, for all four grants together. The checker, the scope and the per-row rights all read that one map, so a list, its rights column and the detail page behind it cannot disagree and a request costs one grant check per type however many questions it asks. `IResourcePermissionStore.GetGrantedResourceKeysAsync` is deliberately not used: it filters on resource + permission name only and is not per-user, so it would report every type that carries a grant for anyone.
- **Rights are decided on the server.** `DocumentListItemDto` and `DocumentDto` carry a `rights` object (`canRead` / `canEdit` / `canReview` / `canDelete` / `canRestore` / `canRetry`) computed by the same checker; the client binds actions to it instead of re-deriving the rule. No `creatorId` is exposed — the client needs to know what it may do with a document, never who owns it.
- **Background jobs are not affected.** They run without a principal and are not user-facing reads.

### Two orderings worth knowing

- **The recycle bin is admitted by entry alone**, and its rows are the Read scope's soft-deleted documents, own included. It used to admit by the Restore arm and narrow by the Read arm — two different questions — so a caller holding only a `Delete` grant was let into a bin the UI then reported as empty while it was not.
- **`GetListAsync` resolves the scope first**, because that is what asserts entry and what narrows the rows. A requested `DocumentTypeCode` the caller can produce no row of comes back as an **empty page**, not a 403 — an owner legitimately lists a type they hold no grant on and gets their own rows back, so a refusal would be wrong for the ordinary case. An unknown **field** name still loud-fails for everyone: it is a correctable signal, and the schema it names is not a secret (see below).

### The document type's schema is not confidential

An unknown-field error names what a type does and does not define. That is deliberately **not** treated as a disclosure to narrow: `IFieldDefinitionAppService.GetListAsync` returns every field definition of the layer to any entry holder by recorded decision (#223 / #629), and the same schema also leaves through the export's column headers, the export's own unknown-field error and the detail page's missing-required-field names. A guard on this one path would have reduced nothing while costing an uploader the correctable message on their own documents.

What *is* confidential is the **rows** — and those are narrowed, everywhere, by the same Read scope.

### What an export contains

`ExportAsync` admits on the **Export** row and narrows the rows in the file by the **Read** scope — the same predicate the screen runs, so "download the current view" keeps meaning the view. It no longer refuses a type the caller holds no `Read` grant on: after ownership that test could not distinguish "you may see nothing of this type" from "you may see your own", so it would have had to admit every caller with an owner arm, which is every real user. An unknown type **code** still loud-fails: "this type does not exist in your layer" is a statement about the layer, not about the caller.

### What a response body contains

Every method of the edit and review families returns the document's DTO. An `Edit`-grant holder with no `Read` grant is refused outright by `GetAsync` — so `MapToDtoAsync` checks the rights first, and when `canRead` is false it returns **only the id and the rights**: no title, body, field values, file origin or review detail. The write still happens; what comes back is not the read the caller was refused.

The **duplicate-candidate panel** on the review detail names other documents by title and file name, so it is narrowed by the caller's Read scope too. A shared `(type, fingerprint)` is not a permission to see whoever else uploaded one. The pipeline's own collision check is deliberately unrestricted: it runs with no principal and only counts, and a duplicate the uploader may not see is still a duplicate.

## Upload

Upload follows the same pattern as every other row. A caller's **type scope** for upload is:

- **every type of the caller's own layer**, and an untyped upload, if the caller holds `Documents.Upload`;
- otherwise, **the types the caller holds an `Upload` resource grant on**, directly or through a role. The grant is enough on its own; `Documents.Upload` is not also required.

| Request | Requirement |
| --- | --- |
| `DocumentTypeId` supplied | The id must resolve to a type in the caller's own layer, **then** `Documents.Upload` **or** the `Upload` grant on that type. Otherwise `AbpAuthorizationException`, with no blob written and no document inserted. |
| `DocumentTypeId` omitted (untyped) | `Documents.Upload`. |

`ConfirmClassification` plays no part in uploading: a reviewer holding it and nothing else is refused every upload.

A declared type keeps the same semantics whichever permission admitted it: classification confidence `1.0`, `ReviewDisposition = Confirmed`, no classification LLM call. The grant is precisely the delegation of that decision, for that one type. See [classification](../pipeline/classification.md).

### An untyped upload needs `Documents.Upload`

An untyped upload hands the type decision to the classifier, which may land the document in any type of the layer — and the document then reaches the downstream consumers that subscribe by `(TenantId, DocumentTypeCode)`. A caller whose upload right is per type must therefore name the type; only the all-types permission uploads untyped.

This is also what `Documents.Upload` meant in every released version (v0.3.x, v0.4.x and the v0.5.0 previews), so upgrading changes nothing for an existing uploader.

### The order `UploadAsync` checks in

1. **Entry.**
2. **The declared type exists** in the caller's own layer (see *Existence is validated before permission*). The visible types are readable by any entry holder, so this discloses nothing.
3. **One judgment by the Upload row**, on the declared type or — for an untyped upload — on no type at all.
4. Only then the business validation: that the layer has any document type, the cabinet (`Cabinets.Default`, then its existence), the file type, the size, and the content-hash duplicate. None of them can answer anything to a caller who may not upload.

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
| Confirm / Reclassify → the type picker | lists every type for `ConfirmClassification` ("Edit Documents of All Types" / 编辑所有类型的文档) **or** `Documents.Upload` ("Upload into All Document Types" / 上传到所有文档类型), otherwise only the types carrying an `Upload` grant — the `DeclareType` row. A question about *types*, so it is answered from `GetVisibleAsync`'s `resourcePermissions` |
| Overview → upload card, document list → Upload button | `Documents.Upload`, **or** an `Upload` grant ("Upload into this Document Type" / 上传到此文档类型) on at least one visible type — see below for how the overview handles a type list that has not arrived or could not be loaded |
| Upload card → the type picker | every type, plus the untyped "Let AI classify" (交由 AI 分类) option, for `Documents.Upload`; otherwise only the types carrying an `Upload` grant, and a type must be chosen. `ConfirmClassification` plays no part in uploading |
| Document list → **Needs review** toggle, overview → **Needs review** quick link | Edit module-wide, or an `Edit` grant on at least one visible type — after this change the review queue is a reviewer's surface, and an uploader whose own document is pending review sees it in the ordinary list with its status |
| Document list → review-count badge, overview → statistics card | `Documents.ReadAll` (both are whole-layer aggregates) |
| Document list → Export; overview → recycle bin | `Documents.Export` and entry, respectively |

The upload entry points need the visible types to answer for a caller whose only upload right is a type-level grant. For such a caller the overview shows a neutral placeholder while the types load, rather than flashing either the upload card or the read-only card; the upload card with its "types could not be loaded" state and retry if the load fails, since a failure says nothing about their grants; and the read-only card once the types have arrived without an `Upload` grant. A `Documents.Upload` holder gets the card straight away: an untyped upload needs no type list.

Because the answer comes from the server, an **untyped** document (unclassified, failed classification, container) offers its actions to its uploader, and a document on an **archived** type offers exactly the actions the API still admits — the two cases a client-side copy of the rule got wrong, the second because it keyed types by `TypeCode` against the active types while the server keys by `Id` across soft-deleted ones.

The **overview's statistics card** is hidden without `Documents.ReadAll`, and the request behind it is not made at all: these are whole-layer aggregates and the endpoint requires `ReadAll`, so an entry-only caller would only collect a 403. Routes are unchanged — `Documents` still gates entry to the documents area.

The rows of the table above that are questions about *types* rather than about a document — the two type pickers, the upload entry points and the **Needs review** affordances — read the visible types from a single root-provided `DocumentTypesStore`, which owns one `GetVisibleAsync` call for the whole session and answers "still loading" and "the fetch failed" for itself. It exists because those are different answers: an empty type list because the request has not returned is not a statement about the caller's grants, and rendering it as one told operators to go ask an administrator for access they already had. A failed fetch does not stick for the session: every page that reads the store retries it on arrival and on each explicit Refresh — only when the last attempt failed, and never from background polling — and the type-management page reloads it after each successful write.

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
