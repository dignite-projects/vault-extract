---
description: "Authorization in Vault Extract: the documents domain's access-rule table (no [Authorize]), and the ABP-default pattern for everything else"
paths:
  - "core/src/**/*Permission*.cs"
  - "core/src/**/*AppService*.cs"
  - "core/src/**/*Controller*.cs"
  - "core/src/**/DocumentAccess*.cs"
  - "host/src/**/*Controller*.cs"
---

# Authorization

## The documents domain is NOT ordinary ABP authorization (#629 / #632 / #635)

The documents domain is different, and the differences are load-bearing. If you are editing `DocumentAppService`, `DocumentExportAppService`, `DocumentReprocessingAppService`, `DocumentStatisticsAppService`, `DocumentPipelineRunAppService` or `CabinetAppService.DeleteAsync`, read `docs/en/configuration/document-type-permissions.md` first. Everything else (`Cabinet`, `DocumentType`, `FieldDefinition`, anything new) uses the ABP default pattern at the bottom of this file.

**1. No `[Authorize]` — ever.** The three documents-domain app services carry no `[Authorize]` attribute on any method, and none at class level. A structural test enforces it (`DocumentTypeAccess_Tests.No_method_of_the_documents_domain_app_services_carries_an_Authorize_attribute`). Two reasons, and the second is the one that bites: MCP / reflection / tool-dispatch paths never run attributes, **and** an attribute fires before the method body, so it would deny a per-type grant holder or a document's own uploader before the body could offer the other arms of the rule.

**2. Authorization is a row on `DocumentAccessRule`, evaluated by `DocumentAccessChecker`.** Every public operation's first authorization act is one of:

```csharp
await _documentAccess.CheckEntryAsync();                                   // before the load
await _documentAccess.CheckAsync(DocumentAccessRule.Edit, DocumentAccessSubject.Of(document));
var scope = await _documentAccess.ResolveScopeAsync(DocumentAccessRule.Read);   // row sets only
```

Adding an operation means **adding a row to the table**, not writing a check. `CheckPolicyAsync(...)` on a `Documents.*` permission in this domain is a bug: it bypasses the ownership and per-type arms, and it asserts no entry.

Every row pairs a role-level arm ("all types") with a type-level grant ("this type"), and the role-level arm is a **set** (#645): the checker admits if any member is granted. When a second all-types permission should also admit an operation, add it to that row's set — `DeclareType` = {`ConfirmClassification`, `Documents.Upload`} is the one such row — never a second check at the call site. Each subject is judged by **one** row: `UploadAsync` is `Upload` on the declared type (or on `DocumentAccessSubject.None` when untyped) and nothing else; a reclassification is `Edit` on the document plus `DeclareType` on the target type, because those are two subjects. AI re-classification (`RerecognizeAsync`) is the same pair with `DeclareType` on `DocumentAccessSubject.None`, since the classifier names the target (#648).

**3. Ownership goes through the rule's owner arm. Never hand-write `CreatorId != CurrentUser.Id`.** The arm is three-valued (`DocumentOwnerArm`: `Never` / `Always` / `UnlessUnderReview`) because an uploader may always read and delete their own document, may never sign off its review, and may not modify it while it is blocked on a review reason other than classification — a hand-written comparison expresses none of that, and each copy of it would drift.

**4. Rights are decided on the server.** `DocumentListItemDto` / `DocumentDto` carry a `rights` object from the same checker. Never re-derive the rule anywhere else, client or server.

**5. Row sets are narrowed by `DocumentAccessScope`**, carried through `DocumentQueries.ApplyMetadataFilter`'s `required` `ReadScope`. A new query over `Document` must set it; a new repository method that returns documents (or names them, like the duplicate-candidate projection) must take it.

## ABP default pattern (everything outside the documents domain)

- Permission names are constants in `VaultExtractPermissions` (`Application.Contracts/Permissions/`), registered in `VaultExtractPermissionDefinitionProvider` with localized display names. Nest children under their group's `Default` permission.
- Declarative: `[Authorize(VaultExtractPermissions.X.Y)]` on the app-service class (`Default`) and per method — see `CabinetAppService`.
- Programmatic where an attribute cannot express it (MCP / tool-dispatch paths, conditional checks): `await CheckPolicyAsync(...)` to throw, `await IsGrantedAsync(...)` to branch.
- Never hard-code role names. Never trust client input for identity — use `CurrentUser`.
- Don't expose sensitive fields in DTOs.
