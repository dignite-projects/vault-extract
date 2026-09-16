# Per-Document-Type Upload Permissions

By default, who may upload a document is an all-or-nothing decision: `VaultExtract.Documents.Upload` opens the endpoint, and `VaultExtract.Documents.ConfirmClassification` decides whether the caller may *declare* the document's type instead of leaving it to LLM classification. That second permission covers **every** type of the caller's layer.

This page describes the narrower grant: **this user (or role) may upload into these document types and no others.**

It is built on ABP's [resource-based authorization](https://abp.io/docs/latest/framework/fundamentals/authorization), not on anything Vault Extract invented — the grants, the management API, the user / role lookup and the dialog are all ABP's.

## What exists

| Kind | Name | Meaning |
| --- | --- | --- |
| Standard permission | `VaultExtract.DocumentTypes.ManagePermissions` | May open the per-type permission dialog and grant / revoke the grants below. |
| Resource permission | `Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType.Upload` | May upload a document declaring **one specific** document type. |

The resource is the `DocumentType` entity; the resource **key** is its immutable `Id`.

`ManagePermissions` is deliberately **not** `DocumentTypes.Update`. Handing out access is a different responsibility from editing a type's schema, and this permission is the only gate ABP puts on the `/api/permission-management/permissions/resource*` endpoints, so it has to be a grant an administrator makes on purpose.

Both strings are **frozen contracts** once the first grant row exists — the same discipline the `Extract:*` error codes follow. They are persisted verbatim in `AbpResourcePermissionGrants`; renaming either one orphans every existing grant silently. In particular the resource name must stay equal to `typeof(DocumentType).FullName`, because ABP derives it from the runtime type of the object handed to the authorization service. A unit test asserts that equality so an entity rename fails the build instead of the ACL.

## The rule `UploadAsync` enforces

A caller's **type scope** for upload is:

- **every type of the caller's own layer** if the caller holds `Documents.ConfirmClassification`;
- otherwise, **the types the caller holds an `Upload` resource grant on**, directly or through a role.

Concretely:

| Request | Requirement |
| --- | --- |
| `DocumentTypeId` supplied | The id must resolve to a type in the caller's own layer, **then** `ConfirmClassification` **or** the `Upload` grant on that type. Otherwise `AbpAuthorizationException`, with no blob written and no document inserted. |
| `DocumentTypeId` omitted (untyped) | `ConfirmClassification`. |

Two properties of that table are worth stating explicitly:

- **Existence is validated before permission.** The lookup runs under ABP's ambient `IMultiTenant` filter, so a cross-layer id resolves to nothing and fails with `EntityNotFoundException` before the permission layer is reached. That is why a grant on a Host-layer type id cannot authorize a tenant caller, and why a probing caller learns nothing from the error about which types exist in another layer.
- **The "or" is written by hand in the application service.** ABP's `ResourcePermissionChecker` only consults the resource value providers; it never falls back to a module-wide permission. The check is programmatic rather than an `[Authorize]` attribute because MCP and reflection dispatch paths do not run attributes.

A resource-granted declaration keeps the same semantics as any other declared type: classification confidence `1.0`, `ReviewDisposition = Confirmed`, no classification LLM call. The grant is precisely the delegation of that decision, for that one type. See [classification](../pipeline/classification.md).

### Untyped upload now requires `ConfirmClassification`

This is a **behaviour change**. An `Upload`-only caller used to be able to upload without a type and let the pipeline classify.

Keeping that would make the per-type grant trivially bypassable: upload untyped, let the LLM classify the document into a type the caller was never granted, and the document still reaches the downstream consumers that subscribe by `(TenantId, DocumentTypeCode)`.

**Migration:** any role that holds `Documents.Upload` without `Documents.ConfirmClassification` and relies on untyped upload must be granted `ConfirmClassification`.

Two narrower alternatives were considered and deferred rather than rejected outright — constraining the classification candidate set to the uploader's scope, and forcing operator confirmation for uploads by callers without `ConfirmClassification`. Both need the uploader's scope captured and persisted at upload time, because the classification job runs without the uploader's principal.

## Picking a type in the UI

`IDocumentTypeAppService.GetVisibleAsync` admits `Documents.Default`, `DocumentTypes.Default` **or** `Documents.Upload` holders — an upload-only caller has to see the list to pick from it.

Every returned `DocumentTypeDto` carries a `resourcePermissions` dictionary filled with the calling principal's own grants on that type, so the client does not have to guess. The upload dialog additionally treats `ConfirmClassification` as "all types".

## Granting and revoking

There is no Vault Extract API for managing these grants. They go through ABP's standard resource-permission endpoints (`/api/permission-management/permissions/resource…`), gated by `ManagePermissions`, and through ABP's `ResourcePermissionManagementComponent` dialog in the operator UI. User and role lookup comes from `Volo.Abp.PermissionManagement.Domain.Identity`, which the host already references.

Grants are stored in **`AbpResourcePermissionGrants`**, an `IMultiTenant` table that has existed since the `Initial` migration — enabling this feature needs no schema change. A row is unique on `(TenantId, Name, ResourceName, ResourceKey, ProviderName, ProviderKey)` and is distributed-cache backed. The provider is `U` for a direct user grant, `R` for a role grant (and `C` for an OAuth client).

## Grant lifecycle

- **Soft delete and restore of a document type keep its grants.** `DocumentType` has no hard delete today; if one is ever added, it must call `IResourcePermissionManager.DeleteAsync` for the resource name and that id, or the rows are orphaned.
- **Role delete / rename and user delete are cleaned up by ABP**, through the event handlers in `Volo.Abp.PermissionManagement.Domain.Identity`.
- **Renaming a type's `TypeCode` is irrelevant.** The resource key is the immutable `Id`, not the code.

## Not in this phase

Only `Upload` is defined. `Read` / `Edit` / `Delete` resource permissions on a document type each need their enforcement points defined first — `Read` in particular means filtering the document list by type, a query-level change. Resource permissions on `Cabinet` or on an individual `Document` are likewise out of scope. Every other operation on a type or on its documents stays governed by the module-wide permissions in `VaultExtractPermissions`: a caller without the full permission cannot do it at all.
