# Per-Document-Type Permissions

By default, what a user may do with documents is an all-or-nothing decision: the module-wide permissions in `VaultExtractPermissions` either admit every document of the layer or none.

This page describes the narrower grants: **this user (or role) may read / edit / delete / upload into these document types and no others.**

They are built on ABP's [resource-based authorization](https://abp.io/docs/latest/framework/fundamentals/authorization), not on anything Vault Extract invented — the grants, the management API, the user / role lookup and the dialog are all ABP's.

## What exists

| Kind | Name | Meaning |
| --- | --- | --- |
| Standard permission | `VaultExtract.Documents` | **Entry.** May enter the documents area and work inside their own type scope. It is also the parent of every permission below it, and the SPA route gate. |
| Standard permission | `VaultExtract.Documents.ReadAll` | May read **every** type of the caller's layer, plus untyped documents. |
| Standard permission | `VaultExtract.DocumentTypes.ManagePermissions` | May open the per-type permission dialog and grant / revoke the grants below. |
| Resource permission | `…DocumentType.Upload` | May upload a document declaring **one specific** document type. |
| Resource permission | `…DocumentType.Read` | May read the documents of **one specific** document type. |
| Resource permission | `…DocumentType.Edit` | May run the operator edit family on **one specific** type's documents. |
| Resource permission | `…DocumentType.Delete` | May soft-delete **one specific** type's documents, and restore them again. |

The four resource permission names are prefixed with the resource name `Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType`. The resource is the `DocumentType` entity; the resource **key** is its immutable `Id`.

`ManagePermissions` is deliberately **not** `DocumentTypes.Update`; the rationale is recorded in [#629](https://github.com/dignite-projects/vault-extract/issues/629).

Every one of those strings is a **frozen contract** once the first grant row exists — the same discipline the `Extract:*` error codes follow. They are persisted verbatim in `AbpResourcePermissionGrants`; renaming any one of them orphans every existing grant silently. In particular the resource name must stay equal to `typeof(DocumentType).FullName`, because ABP derives it from the runtime type of the object handed to the authorization service. A unit test asserts that equality so an entity rename fails the build instead of the ACL.

## `Documents` is entry, `Documents.ReadAll` is read

`VaultExtract.Documents` used to mean both "may enter the documents area" and "may read every document". Those are now two names.

The split is what makes a per-type `Read` grant expressible at all. `Documents` was the gate on every read endpoint and on every SPA documents route, so **every principal that can open the documents area holds it and therefore sees every document of the layer** — a per-type `Read` hung off that alone would never narrow anyone.

ABP documents `PermissionDefinition.Parent` as "this permission can be granted only if the parent is granted", and the permission-management dialog enforces it by checking the parent with the child — but `PermissionChecker` does **not** consult `Parent` at check time, so a permission granted programmatically through `IPermissionManager` can exist without its parent. That nuance does not weaken the paragraph above, which rests on the route guard and the read gates rather than on the parent rule.

> **Breaking change.** A role holding `VaultExtract.Documents` without `ReadAll` now sees only the types it holds a `Read` grant on. The seeded `DocumentManager` and `Viewer` roles gain `ReadAll` automatically on the next migration run (`VaultExtractHostRoleDataSeedContributor` applies it idempotently), and `admin` receives it from the migrator's permission seed. **Hand-made roles and MCP OAuth clients need a manual `ReadAll` grant** or their document lists go empty.

## The rule every enforcement point applies

**An operation on a document is authorized by `VaultExtract.Documents` (entry) AND either the module-wide permission for that operation OR the matching grant on the document's current type.** Within the OR, the module-wide permission remains sufficient on its own; the grant is the narrower alternative.

Entry is a precondition of the whole rule, not only of the read endpoints, and it gates the module-wide half as well as the per-type half: a principal who may not open the documents area may not mutate documents in it either. Every principal assembled through ABP's permission dialog carries it, because each of these module-wide permissions is a child of `VaultExtract.Documents` and the dialog grants the parent with the child; only a programmatic `IPermissionManager` grant can separate them, since `PermissionChecker` never consults `Parent` at check time. It matters most for the per-type half: handing out a resource grant is gated by `DocumentTypes.ManagePermissions`, which says nothing about `Documents.*`, so without this precondition granting "Delete on Invoices" to a principal holding no `Documents` permission at all would produce a caller that could soft-delete, restore and rewrite the Markdown of a document it could not read.

| Operation family | Module-wide | Per-type grant on the document's type |
| --- | --- | --- |
| **Read** — `GetAsync`, `GetBlobAsync`, `GetListAsync` rows, `DocumentPipelineRunAppService.GetListAsync`, MCP `get_document` / `search_documents` / document resources, export rows | `Documents.ReadAll` | `Read` |
| **Edit** — `ConfirmClassificationAsync`, `ReclassifyAsync`, `RerecognizeAsync`, `ReextractFieldsAsync`, `UpdateExtractedFieldsAsync`, `UpdateMarkdownAsync`, `RejectReviewAsync`, `AllowDuplicateAsync`, `ResolveFieldValidationWarningsAsync` | `Documents.ConfirmClassification` | `Edit` |
| **Delete** — `DeleteAsync` (soft delete) | `Documents.Delete` | `Delete` |
| **Restore** — `RestoreAsync`, and reaching the recycle bin at all | `Documents.Restore` | `Delete` — **whoever may delete may undo** |
| **Declare / assign a type** — `UploadAsync` with `DocumentTypeId`, and the **target** type of `ConfirmClassificationAsync` / `ReclassifyAsync` | `Documents.ConfirmClassification` | `Upload` on the **target** type |

Reclassifying a document from type A to type B therefore needs both halves: `Edit` on A (it is an edit of that document) and `Upload` on B (it is a decision about B), or the module-wide permission in place of either.

Restore is the one row whose per-type half is not its own resource permission: it reuses `Delete`. Undoing an operation is not a wider right than the operation, and a per-type deleter who could not restore would have to escalate a mistake of their own making to an admin. Reaching the recycle bin follows the same rule, asked of the layer rather than of one document — `Documents.Restore`, or a `Delete` grant on **at least one** type. Admission is all that decides: the rows inside are still narrowed by the read scope, so a caller who may delete a type but not read it is admitted to an empty recycle bin.

Stays module-wide only, by decision: `PermanentDeleteAsync` (`PermanentDelete`), `RetryPipelineAsync` (`Pipelines.Retry`), everything under `Reprocessing.*`, and the overview statistics (`ReadAll` — a whole-layer aggregate has no per-type meaning). `UpdateCabinetAsync` needs entry plus Read on the document, plus `Cabinets.Default` when a cabinet is assigned. Cabinet reads, field-definition reads and `GetVisibleAsync` keep their existing gates, in which `Documents` now reads as entry.

### Untyped documents are fail-closed

A document with no `DocumentTypeId` — unclassified, failed classification, or a container — belongs to no type, so **no grant can ever cover it**. Only the module-wide permission reaches it: `ReadAll` to read it, `ConfirmClassification` to edit it, `Documents.Delete` to delete it. It is also excluded from the list and export of any caller without `ReadAll`, by construction rather than by a predicate anyone has to remember.

Sub-documents carry their own type and are covered by it.

### Existence is validated before permission

Every lookup runs under ABP's ambient `IMultiTenant` filter, so a cross-layer id resolves to nothing and fails with `EntityNotFoundException` before the permission layer is reached. That is why a grant on a Host-layer type id cannot authorize a tenant caller, and why a probing caller learns nothing from the error about which types exist in another layer.

## Where the rule lives in the code

- **One helper, `DocumentTypeAccessChecker`** (application layer) implements the OR. Every enforcement point calls it; none re-implements it inline. ABP's `ResourcePermissionChecker` only consults the resource value providers and never falls back to a module-wide permission, so the fallback has to be written by hand — but exactly once.
- **The checks are programmatic, not `[Authorize]` attributes.** MCP and reflection dispatch paths do not run attributes, and an attribute would deny a per-type grant holder before the method body could offer the other half of the OR. That is why the edit family and `DeleteAsync` no longer carry one.
- **The read scope is a query predicate, resolved once per request.** `DocumentQueries.ApplyMetadataFilter` — the single chain the operator list, the export and the MCP search all pass through — takes the set of types the caller may read, or `null` for a `ReadAll` holder. The list's total count is narrowed by the same predicate as its rows, so a page count cannot leak how many documents the caller may not see. The review-queue count follows the list.
- **Background jobs are not affected.** They run without a principal and are not user-facing reads.

The per-type half of every check resolves through ABP's own `IResourcePermissionChecker`, over the layer's types (tens, each check a distributed-cache read). `IResourcePermissionStore.GetGrantedResourceKeysAsync` is deliberately not used: it filters on resource + permission name only and is not per-user, so it would report every type that carries a grant for anyone.

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

Client-side rights are a convenience only; the server enforces the same rule regardless of what the client sends.

## What the operator UI shows

The lists themselves are narrowed on the server, so the UI never hides a row it was sent. What it does gate is the per-row actions, from the same `resourcePermissions` dictionary combined with the caller's module-wide permissions — one shared helper (`documentRights`) answers "may read / may edit / may delete this document", and both pages ask it instead of checking a module-wide permission directly.

| Surface | Shown when |
| --- | --- |
| Document list → row actions → Confirm classification | Edit on that row's type |
| Document list → row actions → Delete | Delete on that row's type |
| Document list → selection checkboxes and bulk delete | Delete on at least one row of the page; rows the caller may not delete drop out of the selection |
| Document list → **Needs review** toggle, overview → **Needs review** quick link | Edit module-wide, or an Edit grant on at least one visible type |
| Document list → review-count badge | `Documents.ReadAll` (the count is a whole-layer statistic) |
| Document detail → Delete | Delete on that document's type |
| Document detail → confirm / reclassify / re-recognize / re-extract fields / edit fields / correct Markdown / reject / allow duplicate / resolve warnings | Edit on that document's type |
| Confirm / Reclassify → the type picker | lists only the types the caller may **assign**: `ConfirmClassification`, or an `Upload` grant on that type |
| Overview → statistics card | `Documents.ReadAll` |
| Document list → Export, Upload; overview → recycle bin | unchanged: `Documents.Export`, `Documents.Upload`, `Documents.Restore` |

An **untyped** document (unclassified, failed classification, container) offers its actions only to a caller holding the module-wide permission — the same fail-closed rule the server applies, so the affordance and the endpoint agree.

The **overview's statistics card** is hidden without `Documents.ReadAll`, and the request behind it is not made at all: these are whole-layer aggregates and the endpoint requires `ReadAll`, so an entry-only caller would only collect a 403. Routes are unchanged — `Documents` still gates entry to the documents area.

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
