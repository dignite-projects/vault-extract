---
description: "ABP permission system and authorization patterns"
paths:
  - "**/*Permission*.cs"
  - "**/*AppService*.cs"
  - "**/*Controller*.cs"
---

# ABP Authorization

> **Docs**: https://abp.io/docs/latest/framework/fundamentals/authorization

## ⚠️ The documents domain does NOT follow the patterns below (#629 / #632 / #635)

Everything after this section is the ABP default and applies to `Cabinet`, `DocumentType`, `FieldDefinition` and anything new. **The documents domain is different, and the differences are load-bearing.** If you are editing `DocumentAppService`, `DocumentExportAppService`, `DocumentReprocessingAppService`, `DocumentStatisticsAppService`, `DocumentPipelineRunAppService` or `CabinetAppService.DeleteAsync`, read `docs/en/configuration/document-type-permissions.md` first.

**1. No `[Authorize]` — ever.** The three documents-domain app services carry no `[Authorize]` attribute on any method, and none at class level. A structural test enforces it (`DocumentTypeAccess_Tests.No_method_of_the_documents_domain_app_services_carries_an_Authorize_attribute`). Two reasons, and the second is the one that bites: MCP / reflection / tool-dispatch paths never run attributes, **and** an attribute fires before the method body, so it would deny a per-type grant holder or a document's own uploader before the body could offer the other arms of the rule.

**2. Authorization is a row on `DocumentAccessRule`, evaluated by `DocumentAccessChecker`.** Every public operation's first authorization act is one of:

```csharp
await _documentAccess.CheckEntryAsync();                                   // before the load
await _documentAccess.CheckAsync(DocumentAccessRule.Edit, DocumentAccessSubject.Of(document));
var scope = await _documentAccess.ResolveScopeAsync(DocumentAccessRule.Read);   // row sets only
```

Adding an operation means **adding a row to the table**, not writing a check. `CheckPolicyAsync(...)` on a `Documents.*` permission in this domain is a bug: it bypasses the ownership and per-type arms, and it asserts no entry.

Every row pairs a role-level arm ("all types") with a type-level grant ("this type"), and the role-level arm is a **set** (#645): the checker admits if any member is granted. When a second all-types permission should also admit an operation, add it to that row's set — `DeclareType` = {`ConfirmClassification`, `Documents.Upload`} is the one such row — never a second check at the call site. Each subject is judged by **one** row: `UploadAsync` is `Upload` on the declared type (or on `DocumentAccessSubject.None` when untyped) and nothing else; a reclassification is `Edit` on the document plus `DeclareType` on the target type, because those are two subjects. AI re-classification (`RerecognizeAsync`) is the same pair with `DeclareType` on `DocumentAccessSubject.None`, since the classifier names the target (#648).

**3. Ownership goes through the rule's owner arm. Never hand-write `CreatorId != CurrentUser.Id`.** The "Ownership Validation" snippet further down this file is exactly what not to do here. The arm is three-valued (`DocumentOwnerArm`: `Never` / `Always` / `UnlessUnderReview`) because an uploader may always read and delete their own document, may never sign off its review, and may not modify it while it is blocked on a review reason other than classification — a hand-written comparison expresses none of that, and each copy of it would drift.

**4. Rights are decided on the server.** `DocumentListItemDto` / `DocumentDto` carry a `rights` object from the same checker. Never re-derive the rule anywhere else, client or server.

**5. Row sets are narrowed by `DocumentAccessScope`**, carried through `DocumentQueries.ApplyMetadataFilter`'s `required` `ReadScope`. A new query over `Document` must set it; a new repository method that returns documents (or names them, like the duplicate-candidate projection) must take it.

## Permission Definition
Define permissions in `*.Application.Contracts` project:

```csharp
public static class BookStorePermissions
{
    public const string GroupName = "BookStore";

    public static class Books
    {
        public const string Default = GroupName + ".Books";
        public const string Create = Default + ".Create";
        public const string Edit = Default + ".Edit";
        public const string Delete = Default + ".Delete";
    }
}
```

Register in provider:
```csharp
public class BookStorePermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var bookStoreGroup = context.AddGroup(BookStorePermissions.GroupName, L("Permission:BookStore"));

        var booksPermission = bookStoreGroup.AddPermission(
            BookStorePermissions.Books.Default, 
            L("Permission:Books"));
        
        booksPermission.AddChild(
            BookStorePermissions.Books.Create, 
            L("Permission:Books.Create"));
        
        booksPermission.AddChild(
            BookStorePermissions.Books.Edit, 
            L("Permission:Books.Edit"));
        
        booksPermission.AddChild(
            BookStorePermissions.Books.Delete, 
            L("Permission:Books.Delete"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<BookStoreResource>(name);
    }
}
```

## Using Permissions

### Declarative (Attribute)
```csharp
[Authorize(BookStorePermissions.Books.Create)]
public virtual async Task<BookDto> CreateAsync(CreateBookDto input)
{
    // Only users with Books.Create permission can execute
}
```

### Programmatic Check
```csharp
public class BookAppService : ApplicationService
{
    public async Task DoSomethingAsync()
    {
        // Check and throw if not granted
        await CheckPolicyAsync(BookStorePermissions.Books.Edit);
        
        // Or check without throwing
        if (await IsGrantedAsync(BookStorePermissions.Books.Delete))
        {
            // Has permission
        }
    }
}
```

### Allow Anonymous Access
```csharp
[AllowAnonymous]
public virtual async Task<BookDto> GetPublicBookAsync(Guid id)
{
    // No authentication required
}
```

## Current User
Access authenticated user info via `CurrentUser` property (available in base classes like `ApplicationService`, `DomainService`, `AbpController`):

```csharp
public class BookAppService : ApplicationService
{
    public async Task DoSomethingAsync()
    {
        // CurrentUser is available from base class - no injection needed
        var userId = CurrentUser.Id;
        var userName = CurrentUser.UserName;
        var email = CurrentUser.Email;
        var isAuthenticated = CurrentUser.IsAuthenticated;
        var roles = CurrentUser.Roles;
        var tenantId = CurrentUser.TenantId;
    }
}

// In other services, inject ICurrentUser
public class MyService : ITransientDependency
{
    private readonly ICurrentUser _currentUser;
    public MyService(ICurrentUser currentUser) => _currentUser = currentUser;
}
```

### Ownership Validation
```csharp
public async Task UpdateMyBookAsync(Guid bookId, UpdateBookDto input)
{
    var book = await _bookRepository.GetAsync(bookId);
    
    if (book.CreatorId != CurrentUser.Id)
    {
        throw new AbpAuthorizationException();
    }
    
    // Update book...
}
```

## Multi-Tenancy Permissions
Control permission availability per tenant side:

```csharp
bookStoreGroup.AddPermission(
    BookStorePermissions.Books.Default,
    L("Permission:Books"),
    multiTenancySide: MultiTenancySides.Tenant // Only for tenants
);
```

Options: `MultiTenancySides.Host`, `Tenant`, or `Both`

## Feature-Dependent Permissions
```csharp
booksPermission.RequireFeatures("BookStore.PremiumFeature");
```

## Permission Management
Grant/revoke permissions programmatically:

```csharp
public class MyService : ITransientDependency
{
    private readonly IPermissionManager _permissionManager;
    
    public async Task GrantPermissionToUserAsync(Guid userId, string permissionName)
    {
        await _permissionManager.SetForUserAsync(userId, permissionName, true);
    }
    
    public async Task GrantPermissionToRoleAsync(string roleName, string permissionName)
    {
        await _permissionManager.SetForRoleAsync(roleName, permissionName, true);
    }
}
```

## Security Best Practices
- Never trust client input for user identity
- Use `CurrentUser` property (from base class) or inject `ICurrentUser`
- Validate ownership in application service methods
- Filter queries by current user when appropriate
- Don't expose sensitive fields in DTOs
