```json
//[doc-seo]
{
    "Description": "Connect Claude Desktop, Cursor, Claude Code or any MCP client to Dignite Vault Extract: resources, tools, OAuth discovery, rate limiting and tenant scope."
}
```

# MCP Server

Dignite Vault Extract exposes an **MCP (Model Context Protocol) server** as one of its egress channels, next to the [REST API](rest-api.md) and [integration events](integration-events.md). AI clients (Claude Desktop, Cursor, Claude Code, any MCP client) can read and search documents without custom integration code.

> The MCP server exposes documents, document types and cabinets as resources, plus structured discovery and search tools. Extracted-field searches are anchored to a document type; metadata-only searches may be scoped to a cabinet or a source document instead. There is **no** keyword, full-text, semantic or vector retrieval; that belongs to a downstream RAG consumer. Vault Extract is an MCP **server** only and never acts as an MCP client.

## Installation

The MCP adapter is the `Dignite.Vault.Extract.Mcp` package (`VaultExtractMcpModule`). It does not host a server of its own: it contributes its tools and resources to the shared MCP server of [`Dignite.Abp.AspNetCore.Mcp`](https://github.com/dignite-projects/abp-modules), so one `/mcp` endpoint can serve the tools of several Dignite modules. The shared server owns the transport, the endpoint, permission filtering of `tools/list`, the structured tool-error envelope and the RFC 9728 discovery challenge. Each module owns a name prefix, and the server refuses to start when two modules register the same tool name.

The shipped host depends on `VaultExtractMcpModule` and calls `AddAbpMcpAuthenticationDiscovery` in `VaultExtractHostModule.ConfigureMcpAuthentication`; it does not call `MapMcp` itself. See [Installation](../get-started/installation.md) for the package list.

## What It Exposes

Every tool name starts with `vault_extract_`, and every resource URI uses the `vault-extract://` scheme.

| MCP primitive | Vault Extract mapping |
| --- | --- |
| Resource `vault-extract://documents/{id}` | A short system-metadata header (type, lifecycle, language, created-at, optional cabinet id) followed by the document's Markdown wrapped in `<document>` tags. The wrapped body is external, untrusted content, and the header says so |
| Resource `vault-extract://document-types/{code}` | One document type's field schema |
| Resource `vault-extract://cabinets/{id}` | One cabinet's id, URI, name and optional description, wrapped as untrusted configuration data |
| `resources/list` | Bounded enumeration of the visible document-type and cabinet resources |
| Tool `vault_extract_search_documents` | Structured search; see [Search](#search) |
| Tool `vault_extract_get_document` | One document by id: title, type, lifecycle, language, created-at, the Markdown body and every extracted field value. For clients that cannot read resources |
| Tool `vault_extract_list_document_types` | The visible document types with their complete field schemas (name, field type, multi-value flag, display name, required flag). For clients that cannot list resources |
| Tool `vault_extract_list_cabinets` | Bounded cabinet name-to-id discovery with `totalCount` and `truncated` |

All tools are read-only. The server declares the bare `resources` capability with **no `subscribe` or `listChanged`**: clients read on demand. The transport is **Streamable HTTP** at `/mcp`; the legacy SSE transport is not exposed.

### Search

`vault_extract_search_documents` requires at least one scope anchor: `documentTypeCode`, `originDocumentId` (the sub-documents of one source) or `cabinetId`. Metadata filters (such as `lifecycleStatus`) and extracted-field filters are combined with **AND**. Field filters always require `documentTypeCode`, even when `cabinetId` is supplied; a cabinet-only metadata search may span types.

The result is an object with `items` (thin rows, each with its optional cabinet id and its `vault-extract://documents/{id}` URI), `totalCount` and `truncated`. When `truncated` is `true`, narrow the query instead of treating `items` as complete.

Each entry of `fieldFilters` names a field and gives either an exact `Value` or an inclusive `Min` / `Max` range. The server resolves the field's type from the type's schema; the caller never supplies it.

| Field value type | Supported filter |
| --- | --- |
| Text, Select, Tags, Boolean | Equality (`Value`) only |
| Number, Date / DateTime / Month | Equality or an inclusive `Min` / `Max` range |
| Long text (CKEditor), Table | Not filterable |

Errors are loud, not silent, so an AI client can correct itself instead of mistaking a bad query for "no documents": a field not defined on that type, a field type that cannot be filtered, a range on an unordered type, a value that does not parse as the field's type, an empty filter, an over-length value, more than 10 filters, or field filters without `documentTypeCode` all fail with an error. A valid filter that matches nothing returns an empty list. A field whose **Searchable** flag is off keeps no query index entries, so a filter on it matches nothing.

Field filters are compiled to parameterized comparisons against the derived field-value index (`VaultDocumentFlexFieldIndexes`); field names are never interpolated into SQL, and no `LIKE` is used.

### Limits

These ceilings are compile-time constants (`VaultExtractMcpConsts`), so runtime configuration cannot widen them:

| Limit | Value |
| --- | --- |
| Rows returned by `vault_extract_search_documents` | 50 (`MaxSearchResultCount`); `truncated` and `totalCount` report what was left out |
| Field filters per search | 10 (`MaxSearchFieldFilters`) |
| Document types returned by `vault_extract_list_document_types` and `resources/list` | 100 (`MaxDocumentTypeResults`) |
| Markdown characters of one document body (tool and resource) | 200,000 (`MaxDocumentMarkdownChars`). A clipped body reports `markdownTruncated` and `markdownTotalChars` |

`vault_extract_get_document` also reports `fieldExtractionDeclined` when the document was too large for field extraction, so a client does not read an empty field set as the document's real values.

## Authentication

The `/mcp` endpoint uses the host's existing **OpenIddict Bearer** authentication, the same as the REST API (audience `VaultExtract`). There is **no separate API-key system**: every client presents an OAuth-issued Bearer token, either supplied manually or obtained through automatic discovery. An unattended service client uses the OAuth **client-credentials** grant.

Every request to `/mcp` requires a valid token. In addition, each tool and resource call asserts the **`VaultExtract.Documents`** permission on the server (fail-closed, defense in depth): a token without it gets an authorization error even though the connection was accepted.

> **`VaultExtract.Documents` is entry, not read.** It admits the caller to the MCP surface but returns no document by itself. A client also needs **`VaultExtract.Documents.ReadAll`** to see every type of its layer, or a per-type `Read` grant for each type it should see. Search, `vault_extract_get_document` and the document resources inherit the same read scope as the operator list; documents with no type (unclassified, failed classification, containers) are reachable only with `ReadAll`, or by their uploader. See [Per-Document-Type Permissions](../configuration/document-type-permissions.md).

### Manual Token

Obtain a token from the Vault Extract auth server (`AuthServer:Authority`) with your usual OAuth flow (client credentials for a service client, or an interactive user token), grant the client or user the permissions above, and present it as a static `Authorization: Bearer <token>` header. The `mcp-remote` bridge and a manually configured MCP Inspector work this way. A request that already carries a valid token never triggers the discovery flow.

### Automatic Discovery (RFC 9728)

Spec-compliant MCP clients (Claude Desktop and claude.ai custom connectors, MCP Inspector's *Guided OAuth*, Cursor) can discover the authorization server and sign in interactively:

1. The client connects to `/mcp` **without** a token and receives `401` with `WWW-Authenticate: Bearer resource_metadata="https://<host>/.well-known/oauth-protected-resource/mcp"`.
2. It fetches that **Protected Resource Metadata** document, which advertises the auth server under `authorization_servers`, plus `scopes_supported: ["VaultExtract"]` and `bearer_methods_supported: ["header"]`.
3. It reads the auth server's `/.well-known/openid-configuration` to find the `authorize` and `token` endpoints.
4. It runs Authorization Code + PKCE (a browser sign-in and consent), obtains a token and connects.

The metadata document and the `WWW-Authenticate` pointer come from the `ModelContextProtocol.AspNetCore` authentication scheme (`McpAuth`), wired through `AddAbpMcpAuthenticationDiscovery`. The `McpAuth` scheme does **not** validate tokens and is not part of the `/mcp` authorization policy: it only serves `/.well-known/oauth-protected-resource` and supplies the 401 challenge, and only for the `/mcp` endpoint. Token validation, dynamic claims and tenant resolution stay on the OpenIddict chain, and every other endpoint (admin UI, REST, Swagger) keeps its default challenge.

> **Preset client.** Completing step 4 also needs the auth server to accept the client. Vault Extract seeds **one public, PKCE, native client** for this purpose, with client id **`VaultExtract_Mcp`**, so Guided OAuth works without per-client registration. Dynamic Client Registration (RFC 7591) is deliberately not offered: a self-hosted server faces a knowable set of clients, and an open registration endpoint would be pure attack surface.

#### Configure the Preset Client

Point the client at `https://<host>/mcp` with **no** token and set the client id to `VaultExtract_Mcp` (no secret; it is a public PKCE client):

- **MCP Inspector**: set **Client ID** in the OAuth settings panel, then run *Guided OAuth*.
- **Claude Desktop / claude.ai custom connector**: set **OAuth Client ID** under the connector's *Advanced settings*.
- **mcp-remote / Cursor**: set the configured OAuth client id.
- **ChatGPT connector / OpenAI Codex** and other OAuth-capable clients: set the OAuth **Client ID**.

The browser opens the Vault Extract sign-in page. Because this is a public client with a published client id, you get an **explicit consent screen** before any token is issued.

> The preset client carries only the `VaultExtract` scope plus the minimal `profile` and `email` identity scopes. Data access is still decided by the **signed-in user's** permissions: the client holds none of its own, so a user without `VaultExtract.Documents` is denied even after a successful sign-in.

#### Registered Callbacks

Native desktop clients bind a **random loopback port**, so the seeded client is `ApplicationType = native`, which activates OpenIddict's RFC 8252 loopback relaxation: a callback registered **without a port** matches any `http://<loopback>:<port>/<path>` with the same scheme, host and path.

Without configuration the client is seeded with **one** redirect URI, `https://claude.ai/api/mcp/auth_callback` (claude.ai, Claude Desktop and mobile), the only callback a deployed host normally serves; loopback callbacks on a production client would be unused attack surface. The committed `appsettings.Development.json` replaces it with the development set:

| Redirect URI | Client |
| --- | --- |
| `http://localhost/oauth/callback` | mcp-remote (default host); MCP Inspector automatic flow |
| `http://127.0.0.1/oauth/callback` | mcp-remote started with `--host 127.0.0.1` |
| `http://localhost/oauth/callback/debug` | MCP Inspector manual and debug flow |
| `http://localhost/callback` | Claude Code |
| `https://claude.ai/api/mcp/auth_callback` | claude.ai, Claude Desktop and mobile (fixed hosted callback) |

`127.0.0.1` and `localhost` are **distinct host strings** to OpenIddict. To support another client in production, or one missing above such as **Cursor** (`cursor://anysphere.cursor-mcp/oauth/callback`), set the list in the environment's configuration:

```json
"OpenIddict": {
  "Applications": {
    "VaultExtract_Mcp": {
      "ClientId": "VaultExtract_Mcp",
      "RedirectUris": [
        "http://localhost/oauth/callback",
        "http://127.0.0.1/oauth/callback",
        "cursor://anysphere.cursor-mcp/oauth/callback"
      ]
    }
  }
}
```

`RedirectUris` **replaces** the default rather than merging with it (ASP.NET Core merges configuration arrays by index), so list every URI you still need, the claude.ai callback included, then run the host with `--migrate-database` to re-seed. A client's exact callback can be read from the `redirect_uri` query parameter of its `/authorize` request.

#### Local TLS: Trust the Development Certificate

MCP Inspector and `mcp-remote` run on Node, which does not trust the ASP.NET Core HTTPS development certificate by default; connecting to `https://localhost:44348/mcp` fails with `self-signed certificate` (`MCP error -32099`) before OAuth starts. Tell Node to trust the OS certificate store (PowerShell, Node 22+):

```powershell
$env:NODE_OPTIONS = "--use-system-ca"
npx @modelcontextprotocol/inspector
```

On older Node, `$env:NODE_TLS_REJECT_UNAUTHORIZED = "0"` disables TLS verification for that process. Use it on development machines only. A host with a CA-signed certificate needs neither.

#### Browser-Based Clients Need a CORS Origin

MCP Inspector's web UI and browser-hosted clients run the discovery fetches and token exchange **from the browser**, as cross-origin calls. Unless the client's origin is allowed, they fail with `OAuth Authorization Error: … Failed to fetch` after the sign-in redirect already succeeded. Add the origin to `App:CorsOrigins` (comma-separated); MCP Inspector defaults to `http://localhost:6274`:

```json
"App": {
  "CorsOrigins": "http://localhost:4200,http://localhost:6274"
}
```

Native bridges (`mcp-remote`, Cursor, Claude Desktop) fetch from the server side and are unaffected.

## Tenant Scope

The host runs with multi-tenancy enabled (`VaultExtractHostModule.IsMultiTenant = true`). A call resolves to the tenant of the authenticated principal, and ABP's `IMultiTenant` global filter isolates the data; Vault Extract never writes a tenant predicate by hand. Every credential must therefore carry the right tenant claim.

Every tool also accepts an optional `tenantId`, and each resource has a tenant-scoped twin (`vault-extract://tenants/{tenantId}/documents/{id}`, `.../document-types/{code}`, `.../cabinets/{id}`). Targeting another tenant explicitly is **off by default**. A deployment that needs it must do both of the following:

1. Set `VaultExtractMcpOptions.AllowExplicitTenantScope` to `true`.
2. Replace `IMcpTenantAccessValidator`, whose default implementation (`DenyAllMcpTenantAccessValidator`) denies every request. Vault Extract has no notion of cross-tenant membership, so the deployment decides which callers may target which tenants.

The validator runs before the tenant lookup, so a denied caller cannot tell an unknown tenant from a forbidden one. An unknown or inactive tenant is rejected.

## Rate Limiting

The `/mcp` endpoint is rate-limited per client IP ([#433](https://github.com/dignite-projects/vault-extract/issues/433)), a backstop against abuse that also covers the unauthenticated discovery `401`. It is on by default with generous limits; an over-limit request gets `429`. Configure it under `Mcp:RateLimit`:

```jsonc
"Mcp": {
  "RateLimit": {
    "Enabled": true,       // false disables it entirely
    "PermitLimit": 300,    // requests per window, per client IP
    "WindowSeconds": 60
  }
}
```

### Reverse Proxy Client IPs

By default the host does **not** trust `X-Forwarded-For`. A direct deployment partitions the limiter by the transport peer IP. Behind a reverse proxy that peer is the proxy, so every external client shares one bucket until trusted forwarding is enabled.

Configure the proxy to replace (not append to) `X-Forwarded-For` with the originating address, then declare the proxies that may supply forwarded headers:

```jsonc
"Mcp": {
  "ForwardedClientIp": {
    "Enabled": true,
    "ForwardLimit": 1,                  // number of trusted proxy hops
    "KnownProxies": [ "10.0.0.100" ],   // exact proxy IPs
    "KnownNetworks": [ "10.1.0.0/16" ]  // and/or trusted CIDR ranges
  }
}
```

Environment variables use configuration indexes, for example `Mcp__ForwardedClientIp__Enabled=true` and `Mcp__ForwardedClientIp__KnownProxies__0=10.0.0.100`.

When enabled, the host processes `X-Forwarded-For` and `X-Forwarded-Proto` before routing and rate limiting. Startup fails when neither allow-list is populated, when an address or CIDR is invalid, or when `ForwardLimit` is less than one: trusting forwarded headers from arbitrary senders would let a client spoof its IP and rotate rate-limit partitions. Keep the host reachable only through the declared proxies. IPv4 proxies and networks are also registered in their IPv4-mapped IPv6 form (`::ffff:10.0.0.100`), so the configuration works on dual-mode sockets.

## Connect a Client

### Claude Desktop

Claude Desktop talks to remote HTTP MCP servers through the `mcp-remote` stdio bridge. In `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "vault-extract": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "https://your-vault-extract-host/mcp",
        "--header", "Authorization: Bearer ${VAULT_EXTRACT_TOKEN}"
      ],
      "env": { "VAULT_EXTRACT_TOKEN": "<your-bearer-token>" }
    }
  }
}
```

Restart Claude Desktop; the `vault_extract_*` tools and the `vault-extract://` resources become available.

### Cursor

Cursor reads remote HTTP MCP servers directly. In `.cursor/mcp.json` (project) or the global Cursor MCP settings:

```json
{
  "mcpServers": {
    "vault-extract": {
      "url": "https://your-vault-extract-host/mcp",
      "headers": { "Authorization": "Bearer <your-bearer-token>" }
    }
  }
}
```

### Claude Code

Claude Code reads remote HTTP MCP servers directly and uses Guided OAuth, with an interactive browser sign-in and automatic token refresh. Against a host with a CA-signed certificate, only steps 3 and 4 are needed.

1. **Register Claude Code's callback.** Its callback is `http://localhost:<port>/callback`. The development configuration already lists `http://localhost/callback`; in any other environment add it (no port) to the `VaultExtract_Mcp` redirect URIs as shown in [Registered Callbacks](#registered-callbacks), re-seed with `--migrate-database` and restart the host.
2. **Trust the development certificate (local testing only).** Claude Code's bundled Node honors neither the system certificate store nor `NODE_EXTRA_CA_CERTS`. Add an `env` block to `.claude/settings.local.json` instead; Claude Code passes it to every internal process, including the OAuth one:

   ```json
   {
     "env": { "NODE_TLS_REJECT_UNAUTHORIZED": "0" }
   }
   ```

3. **Add the server**:

   ```bash
   claude mcp add --transport http vault-extract https://localhost:44348/mcp --client-id VaultExtract_Mcp
   ```

4. **Sign in.** Restart Claude Code, run `/mcp`, select **vault-extract** → **Authenticate** and sign in through the browser.

## Typical Flow

1. Resolve the scope:
   - for a named cabinet, call `vault_extract_list_cabinets` (or `resources/list`) and map the display name to its id;
   - for a field query, read the type's schema through `vault_extract_list_document_types` or `vault-extract://document-types/{code}`.
2. Call `vault_extract_search_documents` with at least one of `documentTypeCode`, `originDocumentId` or `cabinetId`, plus `lifecycleStatus` and `fieldFilters` as needed.
3. Read the returned items. If `truncated` is `true`, narrow the query.
4. Read a document's resource (or call `vault_extract_get_document`) to pull the full Markdown and field values.

## Notes and Limits

- **One use case behind every adapter.** The search tool calls the same application service as the REST document list (`IDocumentAppService.GetListAsync`), so permission checks, input validation, field resolution and tenant isolation run in one place. The tool only handles transport concerns: strict `cabinetId` parsing, tolerant `lifecycleStatus` parsing, clamping the row count and wrapping titles with `PromptBoundary`.
- **Strict cabinet id.** An invalid cabinet UUID raises an MCP error instead of silently dropping the filter, which could otherwise widen a cabinet-scoped request to every cabinet.
- **Untrusted content.** Document bodies and configuration text (type and cabinet names) are wrapped and labeled as data. Vault Extract never treats embedded text as instructions, and clients should not either.
- **Single instance.** The Streamable HTTP transport keeps session state in process. Several host instances behind a load balancer need session affinity.
- **Feature gate.** Every tool and resource calls an application service, so a tenant without the [`VaultExtract.Enable`](../configuration/options.md#features) feature is refused. `resources/list` treats a refused category as denied and still lists the others.

## Extending the MCP Surface

A downstream ABP module can add to the surface without forking:

- **Tools**: register tool classes in this module's namespace with `context.Services.AddAbpMcpModule("vault_extract", mcp => mcp.AddTools<TTools>())`. Tool names must start with `vault_extract_`; the shared server enforces it at startup.
- **Resource categories**: add an `IMcpResourceListContributor` to `VaultExtractMcpOptions.ResourceListContributors` and register the contributor in DI (for example with `ITransientDependency`). Each category authorizes itself (a contributor returns `null` when the caller lacks its permission) and must stay bounded. Contributors list `vault-extract://` resources only; a product with a URI scheme of its own registers its own MCP module.

## See Also

- [REST API](rest-api.md)
- [Integration Events](integration-events.md)
- [Per-Document-Type Permissions](../configuration/document-type-permissions.md)
- [Model Context Protocol specification](https://modelcontextprotocol.io/specification)
