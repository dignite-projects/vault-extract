using System;
using System.Collections.Generic;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// #434: the least-privilege contract for an API-key service account, verified as a pure guard (no host needed).
/// The seed contributor gathers the account's real ABP state and delegates the decision here; these lock the
/// decision: exactly <c>VaultExtract.Documents</c> at the user level, no other VaultExtract permission, no roles,
/// and the user must exist.
/// </summary>
public class McpServiceAccountLeastPrivilege_Tests
{
    private static readonly string Account = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa").ToString();

    [Fact]
    public void Exactly_documents_default_with_no_roles_passes()
    {
        Should.NotThrow(() => McpServiceAccountLeastPrivilege.Guard(
            Account,
            label: "codex-prod",
            userExists: true,
            grantedVaultExtractPermissions: new[] { VaultExtractPermissions.Documents.Default },
            roleNames: Array.Empty<string>()));
    }

    [Fact]
    public void A_missing_user_fails_closed()
    {
        var ex = Should.Throw<AbpException>(() => McpServiceAccountLeastPrivilege.Guard(
            Account,
            label: null,
            userExists: false,
            grantedVaultExtractPermissions: Array.Empty<string>(),
            roleNames: Array.Empty<string>()));

        ex.Message.ShouldContain("does not exist");
    }

    [Fact]
    public void Any_role_fails_closed()
    {
        var ex = Should.Throw<AbpException>(() => McpServiceAccountLeastPrivilege.Guard(
            Account,
            label: null,
            userExists: true,
            grantedVaultExtractPermissions: new[] { VaultExtractPermissions.Documents.Default },
            roleNames: new[] { "DocumentManager" }));

        ex.Message.ShouldContain("NO roles");
    }

    [Fact]
    public void A_permission_beyond_the_allowlist_fails_closed()
    {
        // The account also holds Documents.Delete — a write permission an API-key caller must never have.
        var ex = Should.Throw<AbpException>(() => McpServiceAccountLeastPrivilege.Guard(
            Account,
            label: "over-privileged",
            userExists: true,
            grantedVaultExtractPermissions: new[]
            {
                VaultExtractPermissions.Documents.Default,
                VaultExtractPermissions.Documents.Delete
            },
            roleNames: Array.Empty<string>()));

        ex.Message.ShouldContain(VaultExtractPermissions.Documents.Delete);
    }

    [Fact]
    public void A_schema_admin_permission_fails_closed()
    {
        // FieldDefinitions.* / DocumentTypes.* / Cabinets.* are all outside the read allowlist.
        var ex = Should.Throw<AbpException>(() => McpServiceAccountLeastPrivilege.Guard(
            Account,
            label: null,
            userExists: true,
            grantedVaultExtractPermissions: new[]
            {
                VaultExtractPermissions.Documents.Default,
                VaultExtractPermissions.FieldDefinitions.Default
            },
            roleNames: Array.Empty<string>()));

        ex.Message.ShouldContain(VaultExtractPermissions.FieldDefinitions.Default);
    }

    [Fact]
    public void Missing_the_required_documents_default_fails_closed()
    {
        // An existing user with no VaultExtract grant at all is not usable and must fail loudly, not silently.
        var ex = Should.Throw<AbpException>(() => McpServiceAccountLeastPrivilege.Guard(
            Account,
            label: null,
            userExists: true,
            grantedVaultExtractPermissions: new List<string>(),
            roleNames: Array.Empty<string>()));

        ex.Message.ShouldContain(VaultExtractPermissions.Documents.Default);
    }
}
