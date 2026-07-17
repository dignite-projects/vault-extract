using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

            AddEquivalentAddresses(result, address);
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

            AddEquivalentNetworks(result, network);
        }

        return result;
    }

    /// <summary>
    /// Kestrel can expose an IPv4 peer either as an IPv4 address or as its IPv4-mapped IPv6 representation when
    /// dual-mode sockets are in use. Forwarded Headers performs exact proxy/network matching, so trust both
    /// representations of the same configured endpoint rather than making an operator guess the runtime form.
    /// </summary>
    private static void AddEquivalentAddresses(List<IPAddress> result, IPAddress address)
    {
        AddDistinct(result, address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            AddDistinct(result, address.MapToIPv6());
        }
        else if (address.IsIPv4MappedToIPv6)
        {
            AddDistinct(result, address.MapToIPv4());
        }
    }

    private static void AddEquivalentNetworks(List<System.Net.IPNetwork> result, System.Net.IPNetwork network)
    {
        AddDistinct(result, network);
        if (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            AddDistinct(
                result,
                new System.Net.IPNetwork(network.BaseAddress.MapToIPv6(), network.PrefixLength + 96));
        }
        else if (network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= 96)
        {
            AddDistinct(
                result,
                new System.Net.IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96));
        }
    }

    private static void AddDistinct(List<IPAddress> values, IPAddress candidate)
    {
        if (!values.Contains(candidate))
        {
            values.Add(candidate);
        }
    }

    private static void AddDistinct(List<System.Net.IPNetwork> values, System.Net.IPNetwork candidate)
    {
        if (!values.Contains(candidate))
        {
            values.Add(candidate);
        }
    }
}
