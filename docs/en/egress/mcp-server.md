# MCP Server

Dignite Vault Extract exposes an **MCP (Model Context Protocol) server** as one of its channel exits, alongside REST and EventBus (with Webhook planned). It lets AI clients (Claude Desktop, Cursor, any MCP client) read Dignite Vault Extract documents and search them — without writing custom integration code.

> **Dignite Vault Extract is a channel layer.** The MCP server exposes documents, document types, and cabinets as resources plus structured discovery/search tools. Extracted-field searches are anchored to a document type; metadata-only searches may instead be scoped to a cabinet or source container. It does **not** do keyword/full-text or semantic / vector retrieval (those belong to a downstream RAG consumer — see CLAUDE.md "OUT of scope"). It is an MCP **server** only; Dignite Vault Extract never acts as an MCP client.

## What v1 exposes

| MCP primitive | Dignite Vault Extract mapping |
| --- | --- |
| `resources/read` (template `vault-extract://documents/{id}`) | A small system-metadata header (type, lifecycle, language, created-at, optional cabinet id) followed by the document's Markdown body wrapped in `<document>` tags. The wrapped body is external, untrusted content — the header tells clients to treat it as data, not instructions |
| `resources/read` (template `vault-extract://document-types/{code}`) | One document type's filterable field schema |
| `resources/read` (template `vault-extract://cabinets/{id}`) | One cabinet's id, URI, name, and optional description. Names/descriptions are wrapped as untrusted configuration data |
| `resources/list` | Bounded dynamic enumeration of visible document-type and cabinet resources |
| `tools/call` → `list_cabinets` | Bounded cabinet name-to-id discovery with `totalCount` and `truncated`; use the selected id as `search_documents.cabinetId` |
| `tools/call` → `search_documents` | Structured search with at least one scope anchor: `documentTypeCode`, `originDocumentId`, or `cabinetId`. Metadata filters and extracted-field filters are combined with **AND**. Extracted-field filters always require `documentTypeCode`; cabinet-only metadata search may span types. No keyword/full-text search. Returns an object with `items` (up to 50 thin rows, each carrying its optional cabinet id and `vault-extract://documents/{id}` URI) plus `totalCount` and `truncated`; when `truncated` is true, narrow the query rather than treating `items` as complete |

The server declares only the bare `resources` capability — **no `subscribe` / `listChanged`**. v1 is pull-only: clients read on demand. Push (resource subscriptions + `notifications/resources/updated` / `list_changed`) is a follow-up increment (see issue #197).

The transport is **Streamable HTTP** at `/mcp`. (The legacy SSE transport is not exposed.)

## Authentication

The `/mcp` endpoint's primary authentication is the host's existing **OpenIddict Bearer** auth — the same scheme as the REST API (audience `VaultExtract`). An **optional static API-key channel** (a custom request header) can be enabled alongside it for clients that cannot run the OAuth flow — see [§3 *Static API key*](#3-static-api-key-custom-header--fallback-for-non-oauth-clients) below. It is **disabled by default**.

Every request to `/mcp` requires a valid Bearer token (`RequireAuthorization` on the endpoint). In addition, each tool/resource call performs an explicit server-side permission assertion: the caller must be granted **`VaultExtract.Documents`** (`ExtractPermissions.Documents.Default`). A token without that permission gets an authorization error even though the endpoint accepted the connection (fail-closed, defense in depth).

There are two ways for a client to present that token (a third, non-OAuth option — a static API key — is covered separately in §3). Both end at the same Bearer validation — they differ only in **how the client obtains the token**.

### 1. Manual token (static `Authorization` header)

Obtain a token from the Dignite Vault Extract auth server (`AuthServer:Authority`) using your normal OAuth flow (e.g. client-credentials for a service client, or an interactive user token), then grant the client/user the `VaultExtract.Documents` permission via the admin UI. Present it as a static `Authorization: Bearer <token>` header. This is what the `mcp-remote` bridge and a manually-configured MCP Inspector use (see the connection examples below). A request that already carries a valid token is validated directly and **never triggers the discovery flow** — these paths are unchanged.

### 2. Automatic discovery (OAuth Protected Resource Metadata, RFC 9728)

Spec-compliant MCP clients (Claude Desktop native Custom Connectors, claude.ai connectors, MCP Inspector's *Guided OAuth*, Cursor) can discover the authorization server and log in interactively, without a pre-provisioned token:

1. The client connects to `/mcp` **without** a token → receives `401` with a `WWW-Authenticate: Bearer resource_metadata="https://<host>/.well-known/oauth-protected-resource/mcp"` pointer.
2. It fetches that **Protected Resource Metadata** document, which advertises the Dignite Vault Extract auth server (`AuthServer:Authority`) under `authorization_servers`, plus `scopes_supported: ["VaultExtract"]` and `bearer_methods_supported: ["header"]`.
3. It fetches the auth server's `/.well-known/openid-configuration` to find the `authorize` / `token` endpoints.
4. It runs Authorization Code + PKCE (a browser login/consent), obtains a token, and connects.

The discovery metadata and the `WWW-Authenticate` pointer come from the `ModelContextProtocol.AspNetCore` MCP authentication scheme (`McpAuth`), wired in `ExtractHostModule.ConfigureMcpAuthentication`. In this host the `McpAuth` scheme does **not** validate tokens and is **not** part of the `/mcp` authorization policy — it only (a) self-serves `/.well-known/oauth-protected-resource` (its handler runs in the authentication middleware, so there is no separately-mapped controller), and (b) supplies the 401 challenge. Token validation, ABP dynamic claims, and tenant resolution stay on the endpoint's default policy and the existing OpenIddict chain — unchanged. The challenge is routed to `McpAuth` only for the `/mcp` endpoint, by a small `IAuthorizationMiddlewareResultHandler` (`McpDiscoveryAuthorizationResultHandler`) keyed off an endpoint marker; every other endpoint (admin UI, REST, Swagger) keeps the framework-default challenge, so the UI cookie login redirect is untouched. The discovery path is therefore purely additive and never alters the principal used for authorization — the manual-token paths above are byte-for-byte unchanged.

> **Auth-server-side prerequisite — satisfied out of the box (#281).** Exposing the resource metadata is only the resource-server half of the handshake; completing step 4 also needs the OpenIddict authorization server to accept the client. Dignite Vault Extract seeds **one preset public + PKCE + native client** for exactly this — client_id **`VaultExtract_Mcp`** — so Guided OAuth works without per-client registration. Dignite Vault Extract deliberately does **not** run Dynamic Client Registration (RFC 7591): it is self-hosted and faces a knowable set of clients, so an open registration endpoint would be pure attack surface. Instead you paste the preset client_id into each client's OAuth settings — every real target supports a manually specified client_id.

#### Configure the preset client

Point your client at `https://<host>/mcp` with **no** token and supply the client_id `VaultExtract_Mcp` (no client secret — it is a public PKCE client):

- **MCP Inspector** — in the OAuth / Authentication settings panel set **Client ID** to `VaultExtract_Mcp`, then run *Guided OAuth*.
- **Claude Desktop / claude.ai custom connector** — in the connector's *Advanced settings* set the **OAuth Client ID** to `VaultExtract_Mcp` (this field exists precisely for servers that don't offer DCR).
- **mcp-remote / Cursor** — set the configured OAuth client id to `VaultExtract_Mcp`.

The browser opens the Dignite Vault Extract login; you sign in, and — because this is a public client with a published client_id — you get an **explicit consent screen** (`ConsentType = Explicit`) before any token is issued. The client then connects automatically.

> The preset client only carries the `Dignite Vault Extract` scope (plus minimal `profile` / `email` identity scopes). Actual data access is still gated by the **logged-in user's** `VaultExtract.Documents` permission — grant it via the admin UI. The auth-code flow logs in a *user*; the client itself holds no data permission, so a user without `VaultExtract.Documents` is denied fail-closed even after a successful login.

#### Local TLS: trust the dev certificate (test only)

MCP Inspector and `mcp-remote` run on Node, which does **not** trust the ASP.NET Core HTTPS development certificate by default — connecting to `https://localhost:44348/mcp` fails up front with `self-signed certificate` (`MCP error -32099`), before OAuth even starts. This affects local testing against the dev cert only; a host behind a real CA-signed certificate needs none of it.

Tell Node to trust the OS certificate store before launching the tool (PowerShell):

```powershell
# Node 22+: trust the Windows cert store, where the ASP.NET Core dev cert is
# registered after `dotnet dev-certs https --trust`.
$env:NODE_OPTIONS = "--use-system-ca"
npx @modelcontextprotocol/inspector
```

On older Node without `--use-system-ca`, fall back to `$env:NODE_TLS_REJECT_UNAUTHORIZED = "0"`, which disables TLS verification for that process — **dev machines only, never a shared or CI environment**.

#### Browser-based clients need a CORS origin

MCP Inspector's web UI — and any browser-hosted MCP client (e.g. a claude.ai web connector) — runs the OAuth **token exchange and discovery fetches from the browser**, so they are cross-origin calls to the Dignite Vault Extract host. The browser blocks them unless the client's origin is allowed, surfacing as `OAuth Authorization Error: … Failed to fetch` *after* the login/consent redirect already succeeded. Native bridges (`mcp-remote`, Cursor, Claude Desktop) fetch server-side and are unaffected.

Add the client's origin to `App:CorsOrigins` in `appsettings.json` (comma-separated). MCP Inspector defaults to `http://localhost:6274`:

```json
"App": {
  "CorsOrigins": "https://*.Extract.com,http://localhost:4200,http://localhost:6274"
}
```

`http://localhost:4200` (the Angular dev origin) ships by default; append your client's origin next to it. In production, set this via `appsettings.Production.json` or environment variables.

#### Registered callbacks (and how to add your own)

Native desktop clients bind a **random loopback port**, so the seeded client is `ApplicationType = native`, which activates OpenIddict's RFC 8252 loopback relaxation: a callback registered **without a port** matches any `http://<loopback>:<port>/<path>` (scheme / host / path must still match exactly — only the port is relaxed, and only for loopback hosts). The seeded defaults cover the mainstream clients:

| Registered redirect URI | Client |
| --- | --- |
| `http://localhost/oauth/callback` | mcp-remote (default host); MCP Inspector auto flow (any port, incl. 6274) |
| `http://127.0.0.1/oauth/callback` | mcp-remote started with `--host 127.0.0.1` |
| `http://localhost/oauth/callback/debug` | MCP Inspector manual/debug flow |
| `https://claude.ai/api/mcp/auth_callback` | Claude.ai / Claude Desktop / mobile (fixed hosted callback) |

`127.0.0.1` and `localhost` are **distinct host strings** to OpenIddict, so both loopback hosts are listed. To support a client whose callback isn't above — e.g. **Cursor**'s custom-scheme `cursor://anysphere.cursor-mcp/oauth/callback`, or **Claude Code**'s `http://localhost/callback` (note the different path) — override the whole set in `appsettings.json`:

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

`RedirectUris` **replaces** the built-in defaults (it does not merge), so list every URI you still need. Re-run the host with `--migrate-database` to re-seed after changing it. A client's exact callback path can be captured from the `redirect_uri` query parameter on the `/authorize` request during a connect attempt.

> Multi-tenancy is currently disabled (`ExtractHostModule.IsMultiTenant = false`), so all access resolves to the host document space. Tenant isolation is enforced by ABP's ambient `IMultiTenant` global query filter — driven by the authenticated principal's tenant claim, not a hand-written `TenantId` predicate (see `.claude/rules/llm-call-anti-patterns.md`, anti-pattern B) — so it stays correct if multi-tenancy is later enabled, **provided every credential (including an API-key principal) carries the correct tenant claim**.

### 3. Static API key (custom header) — fallback for non-OAuth clients

Some MCP clients cannot run the OAuth flow above but can send a **static custom header** — e.g. **OpenAI Codex** (Streamable-HTTP MCP with arbitrary headers) and **ABP AI Management** (auth type *Custom Header* / *API Key*). For them the host can enable an optional **API-key channel** parallel to the Bearer chain. Claude's native custom connector is OAuth-only and keeps using discovery — it does not use this. (Introduced in #428.)

**Disabled by default.** The shipped `appsettings.json` carries an empty `Mcp:ApiKey:Keys`, so OAuth-only deployments are unaffected. Configure keys via environment variables / user-secrets — **never commit a real key** (the same discipline as `Vault:Extract:ApiKey`):

```jsonc
"Mcp": {
  "ApiKey": {
    "HeaderName": "X-Api-Key",                       // configurable; match it to the client's header
    "RequireHttps": true,                            // ignore a key presented over plain HTTP (default true)
    "SeedServiceAccounts": false,                    // opt-in least-privilege seed/guard (#434), see below
    "Keys": [
      {
        // Configure EITHER the plaintext Key OR its KeyHash (hash-at-rest, #435) — not both.
        "KeyHash": "<lowercase hex sha256 of the key>",  // preferred: a config leak does not expose usable keys
        // "Key": "<a CSPRNG secret, >= 32 chars>",      // alternative: plaintext, env / user-secrets only
        "ServiceAccountUserId": "<guid of a provisioned service-account user>",
        "TenantId": null,                            // host space; set a tenant Guid once multi-tenant
        "Label": "codex-prod"                        // audit attribution; never the key value
      }
    ]
  },
  "RateLimit": {                                     // #433: /mcp rate limiter (per client IP); on by default
    "Enabled": true,
    "PermitLimit": 300,                              // requests per window per IP — generous; a DoS/brute-force cap
    "WindowSeconds": 60
  }
}
```

**How it maps to permissions.** A request carrying a valid key is authenticated as the mapped **service-account user**; a missing or invalid key is ignored — the request falls through to the Bearer chain + discovery `401`, never a `403`. The key grants *authentication* only; actual access is still gated by the same fail-closed `CheckPolicyAsync(VaultExtract.Documents)` the Bearer path hits. So you must:

1. **Provision a dedicated service-account user** (ABP admin UI or your own seed), then
2. **Grant it only `VaultExtract.Documents`** (`ExtractPermissions.Documents.Default`) — directly at the user level, **no roles**. That single permission covers every MCP path (`search_documents`, `get_document`, `list_document_types`, `list_cabinets`, and the document/document-type/cabinet resources). Cabinet discovery is document-read metadata; cabinet create/update/delete operations remain separately protected by `VaultExtract.Cabinets.*`. Granting anything more — or via a shared role — would let an API-key caller exceed an OAuth user; don't.

Set `Mcp:ApiKey:SeedServiceAccounts: true` to have the host **enforce** that least-privilege at startup (opt-in, #434): for every configured `ServiceAccountUserId` it applies the `VaultExtract.Documents` grant and **fails startup** if the account is missing, holds any other VaultExtract permission, or has any role. It never creates users — provision the account first, then copy its Guid into config. Leave it `false` (default) to manage the accounts by hand.

**Operational notes.**

- **TLS only.** The key is a long-lived, bearer-equivalent secret. `Mcp:ApiKey:RequireHttps` (default `true`) makes the server **ignore a key presented over a non-HTTPS request** (it falls through to Bearer), so a key never travels in clear text; behind a TLS-terminating proxy this relies on the host's forwarded-headers handling. Also configure the proxy to strip any inbound copy of the header before forwarding. Set `RequireHttps: false` only for a deliberate plain-HTTP deployment (e.g. local testing).
- **Hash-at-rest (preferred, #435).** Configure a key's **`KeyHash`** (lowercase hex SHA-256 of the key) instead of the plaintext `Key`, so a leak of host config / the secret store does not hand over usable keys — the runtime compares digests, so the two forms are interchangeable. Set exactly one of `Key` / `KeyHash` per entry. Compute the digest without echoing the secret to history:
  - bash: `printf %s "$MCP_KEY" | sha256sum` (take the hex, lowercase)
  - PowerShell: `[BitConverter]::ToString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($env:MCP_KEY))).Replace('-','').ToLower()`
- **Rotation / revocation.** Multiple keys are supported, each with its own `Label` for audit attribution. Rotate with an overlap window (add the new key → migrate clients → remove the old). You can revoke access three ways: remove the key from config, remove the service account's grant, or **disable / delete the service-account user**. Since #431 the API-key channel is a real authentication scheme whose principal flows through ABP dynamic claims, so disabling or deleting the mapped user takes effect on the **next request** — the same real-time revocation a Bearer user gets (no longer config-removal-only).
- **Generation / fail-fast.** Generate keys from a CSPRNG (≥ 256 bits, e.g. 32 random bytes base64url); the host refuses to start on a placeholder value or a plaintext key shorter than 32 characters, a malformed `KeyHash`, both/neither of `Key`/`KeyHash`, or duplicate keys when keys are configured.
- **Rate limiting (#433).** The `/mcp` endpoint is rate-limited per client IP (`Mcp:RateLimit`, on by default, `300`/`60s`), covering both the API-key channel and the discovery `401` path; over-limit requests get `429`. Limits are generous so legitimate MCP session traffic is unaffected — widen `PermitLimit` / `WindowSeconds` or set `Enabled: false` for unusual deployments. Behind a reverse proxy the per-IP partition is only as accurate as the host's forwarded-headers handling. A **present-but-invalid** key is logged as a rate-limited `Warning` (source IP + header name, **never** the value) — an attack signal a simply-absent key does not raise.

#### Connect OpenAI Codex

Add a Streamable-HTTP MCP server pointing at `/mcp` and send the key as a custom header. In `config.toml`:

```toml
[mcp_servers.extract]
url = "https://your-extract-host/mcp"
http_headers = { "X-Api-Key" = "<your-key>" }
# or read it from the environment instead of inlining the secret:
# env_http_headers = { "X-Api-Key" = "EXTRACT_MCP_API_KEY" }
```

(Equivalently, in Codex's UI: transport **Streamable HTTP**, the `/mcp` URL, and a header `X-Api-Key` = `<your-key>`.)

#### Connect ABP AI Management

In *edit MCP server → Auth*, choose **Custom Header**, set the header name to `X-Api-Key` (matching the server's `Mcp:ApiKey:HeaderName`) and the value to your key. If you use the **API Key** auth mode instead and it sends a fixed header name, set `Mcp:ApiKey:HeaderName` on the server to match it.

## Connect Claude Desktop

Claude Desktop talks to remote HTTP MCP servers through the `mcp-remote` stdio bridge. In `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "extract": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "https://your-extract-host/mcp",
        "--header", "Authorization: Bearer ${EXTRACT_TOKEN}"
      ],
      "env": { "EXTRACT_TOKEN": "<your-bearer-token>" }
    }
  }
}
```

Restart Claude Desktop; the `search_documents` tool and `vault-extract://documents/{id}` resources become available.

## Connect Cursor

Cursor reads remote HTTP MCP servers directly. In `.cursor/mcp.json` (project) or the global Cursor MCP settings:

```json
{
  "mcpServers": {
    "extract": {
      "url": "https://your-extract-host/mcp",
      "headers": { "Authorization": "Bearer <your-bearer-token>" }
    }
  }
}
```

## Connect Claude Code

Claude Code (CLI) reads remote HTTP MCP servers directly and uses Guided OAuth — an interactive browser login with automatic token refresh, so no manual bearer token. Against a host with a real CA-signed certificate only steps 3–4 are needed; steps 1–2 cover local testing against the dev certificate.

1. **Register Claude Code's callback.** Its OAuth callback path is `http://localhost:<port>/callback`, which the seeded defaults don't include. Add `http://localhost/callback` to the `VaultExtract_Mcp` redirect URIs (the **Registered callbacks** section above shows the override — the `Native` client type relaxes the port, so register it without one), then re-seed with `--migrate-database` and restart the host.

2. **Trust the dev certificate (local testing only).** Claude Code's bundled Node honours neither the system certificate store nor `NODE_EXTRA_CA_CERTS`, so the **Local TLS** note above (which targets system Node) does not apply here. Instead add an `env` block to `.claude/settings.local.json` — Claude Code injects it into every internal process, including the OAuth sub-process:

   ```json
   {
     "env": { "NODE_TLS_REJECT_UNAUTHORIZED": "0" }
   }
   ```

   This disables TLS verification for that process — dev machines only. A host on a real CA-signed certificate needs none of it.

3. **Add the server** (the preset public PKCE client — no secret, no fixed callback port):

   ```powershell
   claude mcp add --transport http extract https://localhost:44348/mcp --client-id VaultExtract_Mcp
   ```

4. **Log in.** Restart Claude Code, run `/mcp`, select **extract** → **Authenticate**, and sign in through the browser. Tokens are stored and refreshed automatically; the `search_documents` tool and `vault-extract://documents/{id}` resources become available.

## Typical flow

1. Resolve the user's scope:
   - for a named cabinet, call `list_cabinets` (or use `resources/list`) and map the wrapped display name to its id;
   - for an extracted-field query, discover the type/field schema through `list_document_types` or `vault-extract://document-types/{code}`.
2. Call `search_documents` with at least one of `documentTypeCode`, `originDocumentId`, or `cabinetId`. Add `lifecycleStatus` and zero or more `fieldFilters` as needed; multiple filters are AND-ed. `fieldFilters` always require `documentTypeCode`, even when `cabinetId` is also supplied.
3. The tool returns an object with `items` (thin rows, each with an optional `cabinetId` and a `vault-extract://documents/{id}` URI) plus `totalCount` and `truncated`. If `truncated` is true, narrow the query rather than assuming `items` is complete.
4. Call `resources/read` on a document URI to pull the full Markdown; its metadata header preserves the optional `cabinetId`.

## Notes & limits

- **One entry point.** The tool is a thin adapter over the same application-service use case as the REST document list (`IDocumentAppService.GetListAsync`): permission assertion, input validation, field-definition resolution, and tenant isolation all run there. The tool only handles transport concerns — strict `cabinetId` parsing, tolerant `lifecycleStatus` string parsing, clamping the row count, and `PromptBoundary`-wrapping titles.
- **Cabinet-id parsing is deliberately strict.** An invalid cabinet UUID raises an MCP error instead of silently dropping the filter. Dropping it could widen a cabinet-scoped request across all cabinets (when another anchor exists) or produce a misleading missing-scope error. This intentionally differs from the legacy tolerant parsing of `lifecycleStatus`.
- **Result cap.** The search tool returns at most `DocumentConsts.MaxSearchResultCount` (50) rows. The MCP adapter clamps the requested count to this ceiling (an LLM-context safety limit, not a paging window); the REST list endpoint pages normally. When the cap elides matches, the result's `truncated` flag is set and `totalCount` reports the full match count, so a client can tell a complete result from a capped one (parity with `list_document_types`) — issue #445.
- **`ExtractedFields` search performance.** Field values are stored as first-class rows in a `DocumentExtractedField` table (one row per field, value in a typed column — `StringValue` / `BooleanValue` / `NumberValue` / `DateValue` / `DateTimeValue` — keyed by `(DocumentId, FieldDefinitionId)`). Field-value filtering is plain EF Core LINQ: the query anchors on `Documents` (so tenant + soft-delete global filters apply automatically) and compiles each filter to an `EXISTS` over the child rows with an ordinary typed-column comparison (`=` / range). No SQL Server `JSON_VALUE` / `TRY_CONVERT` / native `json` column — the query is portable across SQL Server, PostgreSQL, MySQL, and SQLite, and ordinary B-tree indexes serve both equality and range (issue #206). The wire-format `ExtractedFields` object on a search result is assembled from these rows on read.
- **Field-value semantics.** Each filter in `fieldFilters` names a field plus either an exact `Value` or an inclusive `Min`/`Max` range; multiple filters are combined with **AND** (every filter must match) and all anchor to the one `documentTypeCode`. Each field's query is dispatched by its declared `FieldDataType`, resolved **server-side** from the `(documentTypeCode, name)` `FieldDefinition` — the caller never supplies the type. `Text`/`Boolean` support equality (`Value`) only; `Number`/`Date`/`DateTime` support equality **or** an inclusive `Min`/`Max` range. Passing a range on a `Text`/`Boolean` field is rejected. Queries use only `=` and range comparisons — **never `LIKE`**. Each field name is resolved server-side against its `FieldDefinition` (unknown names raise a business error) and compiled into a parameterized LINQ column comparison — it is never interpolated into SQL, so there is no raw-SQL injection surface. **Errors are loud, not silent**: a malformed request fails with a corrigible error rather than an empty result, so an AI client can self-correct instead of mistaking a bad query for "no documents." A filter with no value, an over-length value, more than `DocumentConsts.MaxSearchFieldFilters` filters, or field filters without a `documentTypeCode` fail validation; a field not defined on that document type, a range on a `Text`/`Boolean` field, or a value that doesn't parse to the field's type raise a business error. A **valid** filter that simply matches nothing returns an empty list (not an error).
- **Input length caps.** Over-length `documentTypeCode` or per-filter field values (`Value` / `Min` / `Max`) are rejected before any scan, keeping an authorized client from forcing expensive table scans through the AI-facing tool.
- **Untrusted body.** A document's Markdown is wrapped in `<document>` tags when read as a resource. Embedded text is never treated as instructions by Dignite Vault Extract, but consuming clients should still treat document content as untrusted.
- **Single instance.** The Streamable HTTP transport keeps session state in-process. Running multiple host instances behind a load balancer requires session affinity (or a future stateless/distributed-store configuration).
