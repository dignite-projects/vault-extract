using System.Collections.Generic;
using System.Linq;
using Dignite.Vault.Extract.Permissions;
using Volo.Abp;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// The least-privilege contract for an API-key service account (#434), as a pure guard so it is unit-testable
/// without an ABP/Identity host. An API-key caller authenticates as a real ABP user and its authority is whatever
/// that user is granted, so the account MUST be pinned to exactly the read surface every MCP path needs and
/// nothing more: it must exist, hold <see cref="VaultExtractPermissions.Documents.Default"/> at the user level,
/// hold <b>no other</b> VaultExtract permission, and have <b>no roles</b> (a shared role could later widen it
/// silently, letting the key exceed an OAuth user). The host seed gathers the account's real state from ABP and
/// calls <see cref="Guard"/>; a violation fails startup fail-closed rather than silently over-permitting.
/// </summary>
public static class McpServiceAccountLeastPrivilege
{
    /// <summary>The one permission an API-key service account may hold — the minimal grant covering every MCP path.</summary>
    public const string AllowedPermission = VaultExtractPermissions.Documents.Default;

    /// <summary>
    /// Throws <see cref="AbpException"/> unless the account is exactly least-privilege. Inputs are the account's
    /// real state as read from ABP: whether the user exists, the VaultExtract permission names granted to it at
    /// the user level, and its role names.
    /// </summary>
    public static void Guard(
        string serviceAccountUserId,
        string? label,
        bool userExists,
        IReadOnlyCollection<string> grantedVaultExtractPermissions,
        IReadOnlyCollection<string> roleNames)
    {
        var who = Describe(serviceAccountUserId, label);

        if (!userExists)
        {
            throw new AbpException(
                $"MCP API-key service account {who} does not exist. Least-privilege seeding does not create users: " +
                "create the service-account user first (e.g. the admin UI), copy its Guid into Mcp:ApiKey:Keys, then re-run seeding.");
        }

        if (roleNames.Count > 0)
        {
            throw new AbpException(
                $"MCP API-key service account {who} holds role(s) [{string.Join(", ", roleNames.OrderBy(r => r))}]. " +
                "An API-key service account MUST have NO roles — a shared role could later widen its permissions beyond an " +
                "OAuth user. Remove the role assignment(s), granting only " + AllowedPermission + " directly at the user level.");
        }

        var extras = grantedVaultExtractPermissions.Where(p => p != AllowedPermission).OrderBy(p => p).ToList();
        if (extras.Count > 0)
        {
            throw new AbpException(
                $"MCP API-key service account {who} holds VaultExtract permission(s) beyond the read allowlist: " +
                $"[{string.Join(", ", extras)}]. It may hold ONLY {AllowedPermission}. Revoke the extra permission(s) so an " +
                "API-key caller can never exceed an OAuth user.");
        }

        if (!grantedVaultExtractPermissions.Contains(AllowedPermission))
        {
            throw new AbpException(
                $"MCP API-key service account {who} is missing the required {AllowedPermission} grant (the minimal permission " +
                "covering every MCP path). Grant it directly at the user level, with no roles.");
        }
    }

    private static string Describe(string serviceAccountUserId, string? label)
    {
        return string.IsNullOrWhiteSpace(label)
            ? $"'{serviceAccountUserId}'"
            : $"'{serviceAccountUserId}' (label '{label}')";
    }
}
