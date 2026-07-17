using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Configures ASP.NET Core forwarded-header processing for the MCP per-IP rate limiter (#469).
/// The MCP exit owns the fail-closed trust-list mechanism; the host still owns the deployment
/// configuration and remains responsible for placing <c>UseForwardedHeaders</c> before routing.
/// </summary>
public static class McpForwardedClientIpServiceCollectionExtensions
{
    public static IServiceCollection AddVaultExtractMcpForwardedClientIp(
        this IServiceCollection services,
        IConfigurationSection configuration,
        bool includeForwardedProto = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = configuration.Get<McpForwardedClientIpOptions>() ?? new McpForwardedClientIpOptions();
        if (!settings.Enabled)
        {
            if (includeForwardedProto)
            {
                // Preserve the host's existing development/non-HTTPS-metadata scheme forwarding,
                // but retain ASP.NET Core's loopback-only trust defaults instead of trusting any sender.
                services.Configure<ForwardedHeadersOptions>(options =>
                    options.ForwardedHeaders |= ForwardedHeaders.XForwardedProto);
            }

            return services;
        }

        if (settings.ForwardLimit <= 0)
        {
            throw new InvalidOperationException(
                $"{configuration.Path}:ForwardLimit must be greater than zero when forwarded client IP handling is enabled.");
        }

        var knownProxies = ParseKnownProxies(configuration.Path, settings.KnownProxies);
        var knownNetworks = ParseKnownNetworks(configuration.Path, settings.KnownNetworks);
        if (knownProxies.Count == 0 && knownNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                $"{configuration.Path} is enabled but no trusted source is configured. " +
                "Set KnownProxies and/or KnownNetworks; accepting X-Forwarded-For from every sender would permit IP spoofing.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = settings.ForwardLimit;

            // The configured allowlist is authoritative. Do not retain implicit loopback entries that
            // the operator did not include in the deployment's declared proxy trust boundary.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in knownProxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in knownNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });

        return services;
    }

    private static List<IPAddress> ParseKnownProxies(string sectionPath, IEnumerable<string> values)
    {
        var result = new List<IPAddress>();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!IPAddress.TryParse(value.Trim(), out var address))
            {
                throw new InvalidOperationException(
                    $"{sectionPath}:KnownProxies contains invalid IP address '{value}'.");
            }

            result.Add(address);
        }

        return result;
    }

    private static List<System.Net.IPNetwork> ParseKnownNetworks(string sectionPath, IEnumerable<string> values)
    {
        var result = new List<System.Net.IPNetwork>();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var cidr = value.Trim();
            if (!cidr.Contains('/') || !System.Net.IPNetwork.TryParse(cidr, out var network))
            {
                throw new InvalidOperationException(
                    $"{sectionPath}:KnownNetworks contains invalid CIDR network '{value}'.");
            }

            result.Add(network);
        }

        return result;
    }
}
