using Dignite.Vault.Extract.Documents.DocumentTypes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Authorization.Permissions.Resources;
using Volo.Abp.Data;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Users;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <see cref="DocumentAccessMemo"/> with one extra reason to forget its answers: the test host changed what the
/// principal is granted.
/// <para>
/// The production memo is scoped, and its only invalidation is the ambient identity, because <b>a real request
/// never changes what its principal is granted half way through</b>. A test that refuses a call, adds a grant and
/// repeats the call is simulating a second request inside one scope — so it watches
/// <see cref="GrantSetAuthorizationService.Version"/> and <see cref="InMemoryResourcePermissionStore.Version"/>
/// and resets when either moves.
/// </para>
/// <para>
/// <b>It does not defeat the memo.</b> Between two grant changes it behaves exactly like the production class, so
/// the cost facts (<c>One_request_costs_…</c>) still measure the real thing. Anything it hid would be a claim
/// about caching across a grant change, which production has no way to reach.
/// </para>
/// </summary>
public class TestDocumentAccessMemo : DocumentAccessMemo
{
    private readonly GrantSetAuthorizationService _authorization;
    private readonly InMemoryResourcePermissionStore _resourcePermissionStore;

    private int _seenGrantVersion = -1;
    private int _seenResourceVersion = -1;

    public TestDocumentAccessMemo(
        IResourcePermissionChecker resourcePermissionChecker,
        IAuthorizationService authorizationService,
        IDocumentTypeRepository documentTypeRepository,
        IDataFilter dataFilter,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        GrantSetAuthorizationService authorization,
        InMemoryResourcePermissionStore resourcePermissionStore)
        : base(
            resourcePermissionChecker,
            authorizationService,
            documentTypeRepository,
            dataFilter,
            currentTenant,
            currentUser)
    {
        _authorization = authorization;
        _resourcePermissionStore = resourcePermissionStore;
    }

    protected override void ResetIfIdentityChanged()
    {
        base.ResetIfIdentityChanged();

        if (_seenGrantVersion == _authorization.Version && _seenResourceVersion == _resourcePermissionStore.Version)
        {
            return;
        }

        ResetMemo();
        _seenGrantVersion = _authorization.Version;
        _seenResourceVersion = _resourcePermissionStore.Version;
    }
}

/// <summary>Registers <see cref="TestDocumentAccessMemo"/> in place of the production memo.</summary>
public static class TestDocumentAccessMemoRegistration
{
    public static void UseTestAccessMemo(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<DocumentAccessMemo, TestDocumentAccessMemo>());
    }
}
