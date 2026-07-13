using System;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Parses an explicit MCP tenant argument and temporarily changes the ABP tenant context for one
/// tool or resource invocation. Authorization and data filtering remain the responsibility of the
/// application use case and ABP's enabled <c>IMultiTenant</c> filter respectively.
/// </summary>
internal static class McpTenantScope
{
    public static Guid? Parse(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        if (!Guid.TryParse(tenantId, out var parsedTenantId))
        {
            throw new McpException($"Invalid tenant id: {tenantId}");
        }

        return parsedTenantId;
    }

    public static IDisposable? Change(Guid? tenantId, IServiceProvider? serviceProvider)
    {
        if (!tenantId.HasValue)
        {
            return null;
        }

        if (serviceProvider is null)
        {
            throw new InvalidOperationException(
                "An IServiceProvider is required when an explicit tenant id is supplied.");
        }

        return serviceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId);
    }
}
