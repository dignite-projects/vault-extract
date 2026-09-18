using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #635 decision 4's other half: <b>both memos reset when the tenant or the principal changes inside a scope.</b>
/// The principal half is pinned through the real permission chain by
/// <c>Mcp.Integration.Tests/Documents/MachineIdentityOwnership_Tests</c>; the tenant half has no fact anywhere,
/// even though <see cref="DocumentAccessMemo.ResetIfIdentityChanged"/> records the tenant as part of the identity
/// tuple it compares (<c>(_currentTenant.Id, _currentUser.Id, _currentUser.FindClaimValue(...))</c>) for exactly
/// this reason: <c>McpTenantScope</c> changes the ambient tenant <b>inside</b> one DI scope on the explicit-tenant
/// MCP path, and a memo that only watched the principal would hand a switched-tenant caller the first tenant's
/// per-type grants.
/// <para>
/// One test, one scope, three calls through the same <see cref="DocumentAccessTestBase.AppService"/> instance --
/// mirroring what <c>TestDocumentAccessMemo</c>'s own doc comment calls "simulating a second request inside one
/// scope". The first two calls pin the memo HIT (same tenant, repeated); the third, wrapped in
/// <c>ICurrentTenant.Change</c>, pins the RELOAD. Both the per-type grant memo (counted through
/// <see cref="CountingResourcePermissionChecker"/>) and the standard-permission memo (counted through
/// <see cref="GrantSetAuthorizationService.PolicyChecks"/>) are asserted, because a memo half-reset would still
/// answer correctly -- from the wrong tenant's cached grants -- and neither counter alone would catch that.
/// </para>
/// </summary>
public class DocumentAccessMemoTenantReset_Tests : DocumentAccessTestBase
{
    [Fact]
    public async Task Both_memos_reset_when_the_tenant_changes_inside_one_scope()
    {
        // Somebody else's document, so the call reaches the per-type arm (the resource-grant memo) rather than
        // short-circuiting on ownership -- Reaching_ones_own_document_costs_no_grant_check_and_no_type_layer_read
        // (DocumentAccessEntryGate_Tests) is exactly the case that must NOT happen here.
        var document = StubDocument(TypeA.Id, creatorId: OwnerId);
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        var currentTenant = GetRequiredService<ICurrentTenant>();

        CheckCounter.Reset();
        Authorization.ResetPolicyChecks();

        await AsStrangerAsync(() => AppService.GetAsync(document.Id));

        var loadedMultiNameChecks = CheckCounter.MultiNameChecks;
        var loadedPolicyChecks = Authorization.PolicyChecks;
        loadedMultiNameChecks.ShouldBeGreaterThan(0, "the first call must have loaded the per-type grant memo");
        loadedPolicyChecks.ShouldBeGreaterThan(0, "the first call must have loaded the standard-permission memo");

        // Same tenant, same principal, repeated inside the same scope: both memos are hit, not reloaded.
        await AsStrangerAsync(() => AppService.GetAsync(document.Id));
        CheckCounter.MultiNameChecks.ShouldBe(loadedMultiNameChecks, "a repeat call in the same tenant must be a memo hit");
        Authorization.PolicyChecks.ShouldBe(loadedPolicyChecks, "a repeat call in the same tenant must be a memo hit");

        // The tenant changes INSIDE this one scope -- McpTenantScope's shape, not a second test. Both memos must
        // reload rather than answer from the first tenant's grants.
        using (currentTenant.Change(Guid.NewGuid()))
        {
            await AsStrangerAsync(() => AppService.GetAsync(document.Id));
        }

        CheckCounter.MultiNameChecks.ShouldBeGreaterThan(
            loadedMultiNameChecks, "a tenant change inside the scope must reload the per-type grant memo");
        Authorization.PolicyChecks.ShouldBeGreaterThan(
            loadedPolicyChecks, "a tenant change inside the scope must reload the standard-permission memo");
    }
}
