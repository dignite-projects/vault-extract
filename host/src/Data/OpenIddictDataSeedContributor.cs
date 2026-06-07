using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.OpenIddict;
using Volo.Abp.OpenIddict.Applications;
using Volo.Abp.OpenIddict.Scopes;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Host.Data;

/* Creates initial data that is needed to property run the application
 * and make client-to-server communication possible.
 */
public class OpenIddictDataSeedContributor : OpenIddictDataSeedContributorBase, IDataSeedContributor, ITransientDependency
{
    public OpenIddictDataSeedContributor(
        IConfiguration configuration,
        IOpenIddictApplicationRepository openIddictApplicationRepository,
        IAbpApplicationManager applicationManager,
        IOpenIddictScopeRepository openIddictScopeRepository,
        IOpenIddictScopeManager scopeManager)
        : base(configuration, openIddictApplicationRepository, applicationManager, openIddictScopeRepository, scopeManager)
    {
    }

    [UnitOfWork]
    public virtual async Task SeedAsync(DataSeedContext context)
    {
        await CreateScopesAsync();
        await CreateApplicationsAsync();
    }

    private async Task CreateScopesAsync()
    {
        // The Paperbase API is a SINGLE OpenIddict resource named "Paperbase" — this is the token
        // audience for every client (Angular, Swagger, and MCP alike). MCP's RFC 8707 `resource`
        // parameter does NOT introduce a second audience: the resource gates are turned off in
        // PaperbaseHostModule (the parameter is accepted-but-ignored), so an MCP-issued token's aud
        // stays "Paperbase", matching the validation layer's AddAudiences("Paperbase").
        var scopeDescriptor = new OpenIddictScopeDescriptor
        {
            Name = "Paperbase",
            DisplayName = "Paperbase API"
        };
        scopeDescriptor.Resources.Add("Paperbase");

        await CreateScopesAsync(scopeDescriptor);
    }

    private async Task CreateApplicationsAsync()
    {
        var commonScopes = new List<string>
        {
            OpenIddictConstants.Permissions.Scopes.Address,
            OpenIddictConstants.Permissions.Scopes.Email,
            OpenIddictConstants.Permissions.Scopes.Phone,
            OpenIddictConstants.Permissions.Scopes.Profile,
            OpenIddictConstants.Permissions.Scopes.Roles,
            "Paperbase"
        };

        var configurationSection = Configuration.GetSection("OpenIddict:Applications");

        // Angular Client
        var consoleAndAngularClientId = configurationSection["Paperbase_App:ClientId"];
        if (!consoleAndAngularClientId.IsNullOrWhiteSpace())
        {
            var webClientRootUrl = configurationSection["Paperbase_App:RootUrl"]?.TrimEnd('/');
            await CreateOrUpdateApplicationAsync(
                applicationType: OpenIddictConstants.ApplicationTypes.Web,
                name: consoleAndAngularClientId,
                type: OpenIddictConstants.ClientTypes.Public,
                consentType: OpenIddictConstants.ConsentTypes.Implicit,
                displayName: "Console Test / Angular Application",
                secret: null,
                grantTypes: new List<string>
                {
                    OpenIddictConstants.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.GrantTypes.Password,
                    OpenIddictConstants.GrantTypes.ClientCredentials,
                    OpenIddictConstants.GrantTypes.RefreshToken,
                    "LinkLogin",
                    "Impersonation"
                },
                scopes: commonScopes,
                redirectUris: new List<string> { webClientRootUrl },
                postLogoutRedirectUris: new List<string> { webClientRootUrl }
            );
        }

        // Swagger Client
        var swaggerClientId = configurationSection["Paperbase_Swagger:ClientId"];
        if (!swaggerClientId.IsNullOrWhiteSpace())
        {
            var swaggerRootUrl = configurationSection["Paperbase_Swagger:RootUrl"]?.TrimEnd('/');

            await CreateOrUpdateApplicationAsync(
                applicationType: OpenIddictConstants.ApplicationTypes.Web,
                name: swaggerClientId,
                type: OpenIddictConstants.ClientTypes.Public,
                consentType: OpenIddictConstants.ConsentTypes.Implicit,
                displayName: "Swagger Application",
                secret: null,
                grantTypes: new List<string>
                {
                    OpenIddictConstants.GrantTypes.AuthorizationCode,
                },
                scopes: commonScopes,
                redirectUris: new List<string> { $"{swaggerRootUrl}/swagger/oauth2-redirect.html" }
            );
        }

        // MCP native client (#281): one preset public + PKCE + native client that enables Guided OAuth
        // (Authorization Code + PKCE interactive browser login) for native MCP clients — MCP Inspector,
        // mcp-remote, Claude Desktop / claude.ai connectors — that connect to /mcp without a token. This
        // is the authorization-server half of the RFC 9728 discovery flow whose resource-server half was
        // wired in #278/#280. We deliberately do NOT implement Dynamic Client Registration (RFC 7591):
        // Paperbase is a self-hosted channel facing a knowable set of clients, so an open registration
        // endpoint is pure attack surface. Instead the client_id is documented (docs/mcp-server.md) and
        // operators paste it into each client's OAuth settings — every real target supports a manually
        // specified client_id.
        //
        // ApplicationType MUST be Native: that is what activates OpenIddict's RFC 8252 loopback port
        // relaxation — a redirect_uri registered WITHOUT a port (e.g. http://127.0.0.1/oauth/callback)
        // then matches any ephemeral port http://127.0.0.1:<port>/oauth/callback (scheme/host/path/query/
        // fragment must still be byte-equal; only the port is relaxed, and only for loopback hosts). Native
        // desktop clients bind a random loopback port, so this single preset client covers them all.
        // Web/default-type clients (Paperbase_App / Paperbase_Swagger above) do NOT get this relaxation.
        //
        // ConsentType is Explicit: this is a PUBLIC client whose client_id is published and non-secret, so
        // any application can present it. We require an interactive consent screen rather than silently
        // issuing tokens (OAuth 2.1 BCP for public clients). Data access stays gated fail-closed by the
        // logged-in user's Paperbase.Documents permission — the auth-code flow logs in a user, and the
        // client itself holds no data permission.
        var mcpClientId = configurationSection["Paperbase_Mcp:ClientId"];
        if (!mcpClientId.IsNullOrWhiteSpace())
        {
            // Operators may override the entire callback set via
            // OpenIddict:Applications:Paperbase_Mcp:RedirectUris (replace semantics — list every URI you
            // want, including any built-ins you still need). When unset we seed the researched defaults
            // below. Loopback entries are registered WITHOUT a port and rely on the Native-type port
            // relaxation to match the client's ephemeral port; 127.0.0.1 and localhost are DISTINCT host
            // strings to OpenIddict, so both are listed. Paths are matched exactly. Verified against each
            // client's source (#281):
            //   http://localhost/oauth/callback        → mcp-remote (default host) + MCP Inspector auto flow (any port, incl. 6274)
            //   http://127.0.0.1/oauth/callback         → mcp-remote --host 127.0.0.1
            //   http://localhost/oauth/callback/debug   → MCP Inspector manual/debug flow
            //   https://claude.ai/api/mcp/auth_callback → Claude.ai / Claude Desktop / mobile (fixed hosted callback)
            // Cursor (custom cursor:// scheme) and Claude Code CLI (/callback path) are NOT seeded by
            // default — add them via the config override if needed (see docs/mcp-server.md).
            var mcpRedirectUris = configurationSection
                .GetSection("Paperbase_Mcp:RedirectUris")
                .Get<List<string>>();

            if (mcpRedirectUris == null || mcpRedirectUris.Count == 0)
            {
                mcpRedirectUris = new List<string>
                {
                    "http://localhost/oauth/callback",
                    "http://127.0.0.1/oauth/callback",
                    "http://localhost/oauth/callback/debug",
                    "https://claude.ai/api/mcp/auth_callback"
                };
            }

            await CreateOrUpdateApplicationAsync(
                applicationType: OpenIddictConstants.ApplicationTypes.Native,
                name: mcpClientId,
                type: OpenIddictConstants.ClientTypes.Public,
                consentType: OpenIddictConstants.ConsentTypes.Explicit,
                displayName: "Paperbase MCP (native clients)",
                secret: null,
                grantTypes: new List<string>
                {
                    OpenIddictConstants.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.GrantTypes.RefreshToken
                },
                // Mirror the advertised resource metadata (scopes_supported: ["Paperbase"]) plus minimal
                // OIDC identity scopes for the interactive login. Address/Phone/Roles are intentionally
                // omitted (least privilege + a clean Explicit-consent screen). openid is implicit in
                // OpenIddict and needs no scope permission; offline_access is gated by the RefreshToken
                // grant above.
                scopes: new List<string>
                {
                    OpenIddictConstants.Permissions.Scopes.Profile,
                    OpenIddictConstants.Permissions.Scopes.Email,
                    "Paperbase"
                },
                redirectUris: mcpRedirectUris
            );
        }
    }
}
