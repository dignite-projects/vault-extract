# MCP Server

Dignite Vault Extract exposes an **MCP (Model Context Protocol) server** as one of its channel exits, alongside REST and EventBus (with Webhook planned). It lets AI clients (Claude Desktop, Cursor, any MCP client) read Dignite Vault Extract documents and search them — without writing custom integration code.

> **Dignite Vault Extract is a channel layer.** The MCP server exposes documents as resources plus a structured **search tool** (metadata + extracted-field values, anchored to a document type). It does **not** do keyword/full-text or semantic / vector retrieval (those belong to a downstream RAG consumer — see CLAUDE.md "OUT of scope"). It is an MCP **server** only; Dignite Vault Extract never acts as an MCP client.

## What v1 exposes

| MCP primitive | Dignite Vault Extract mapping |
| --- | --- |
| `resources/read` (template `extract://documents/{id}`) | A small system-metadata header (type, lifecycle, language, created-at) followed by the document's Markdown body wrapped in `<document>` tags. The wrapped body is external, untrusted content — the header tells clients to treat it as data, not instructions |
| `tools/call` → `search_extract_documents` | Structured search **within a required `documentTypeCode`**: metadata (`lifecycleStatus`) + zero or more `ExtractedFields` field-value filters, all combined with **AND** (each is an equality, or a numeric/date `min`/`max` range). No keyword/full-text search. Returns up to 50 thin rows; each row carries the `extract://documents/{id}` uri to read the full document |

The server declares only the bare `resources` capability — **no `subscribe` / `listChanged`**. v1 is pull-only: clients read on demand. Push (resource subscriptions + `notifications/resources/updated` / `list_changed`) is a follow-up increment (see issue #197).

The transport is **Streamable HTTP** at `/mcp`. (The legacy SSE transport is not exposed.)

## Authentication

The `/mcp` endpoint reuses the host's existing **OpenIddict Bearer** auth — the same scheme as the REST API (audience `Dignite Vault Extract`). There is no separate API-key system in v1.

Every request to `/mcp` requires a valid Bearer token (`RequireAuthorization` on the endpoint). In addition, each tool/resource call performs an explicit server-side permission assertion: the caller must be granted **`Extract.Documents`** (`ExtractPermissions.Documents.Default`). A token without that permission gets an authorization error even though the endpoint accepted the connection (fail-closed, defense in depth).

There are two ways for a client to present that token. Both end at the same Bearer validation — they differ only in **how the client obtains the token**.

### 1. Manual token (static `Authorization` header)

Obtain a token from the Dignite Vault Extract auth server (`AuthServer:Authority`) using your normal OAuth flow (e.g. client-credentials for a service client, or an interactive user token), then grant the client/user the `Extract.Documents` permission via the admin UI. Present it as a static `Authorization: Bearer <token>` header. This is what the `mcp-remote` bridge and a manually-configured MCP Inspector use (see the connection examples below). A request that already carries a valid token is validated directly and **never triggers the discovery flow** — these paths are unchanged.

### 2. Automatic discovery (OAuth Protected Resource Metadata, RFC 9728)

Spec-compliant MCP clients (Claude Desktop native Custom Connectors, claude.ai connectors, MCP Inspector's *Guided OAuth*, Cursor) can discover the authorization server and log in interactively, without a pre-provisioned token:

1. The client connects to `/mcp` **without** a token → receives `401` with a `WWW-Authenticate: Bearer resource_metadata="https://<host>/.well-known/oauth-protected-resource/mcp"` pointer.
2. It fetches that **Protected Resource Metadata** document, which advertises the Dignite Vault Extract auth server (`AuthServer:Authority`) under `authorization_servers`, plus `scopes_supported: ["Extract"]` and `bearer_methods_supported: ["header"]`.
3. It fetches the auth server's `/.well-known/openid-configuration` to find the `authorize` / `token` endpoints.
4. It runs Authorization Code + PKCE (a browser login/consent), obtains a token, and connects.

The discovery metadata and the `WWW-Authenticate` pointer come from the `ModelContextProtocol.AspNetCore` MCP authentication scheme (`McpAuth`), wired in `ExtractHostModule.ConfigureMcpAuthentication`. In this host the `McpAuth` scheme does **not** validate tokens and is **not** part of the `/mcp` authorization policy — it only (a) self-serves `/.well-known/oauth-protected-resource` (its handler runs in the authentication middleware, so there is no separately-mapped controller), and (b) supplies the 401 challenge. Token validation, ABP dynamic claims, and tenant resolution stay on the endpoint's default policy and the existing OpenIddict chain — unchanged. The challenge is routed to `McpAuth` only for the `/mcp` endpoint, by a small `IAuthorizationMiddlewareResultHandler` (`McpDiscoveryAuthorizationResultHandler`) keyed off an endpoint marker; every other endpoint (admin UI, REST, Swagger) keeps the framework-default challenge, so the UI cookie login redirect is untouched. The discovery path is therefore purely additive and never alters the principal used for authorization — the manual-token paths above are byte-for-byte unchanged.

> **Auth-server-side prerequisite — satisfied out of the box (#281).** Exposing the resource metadata is only the resource-server half of the handshake; completing step 4 also needs the OpenIddict authorization server to accept the client. Dignite Vault Extract seeds **one preset public + PKCE + native client** for exactly this — client_id **`Extract_Mcp`** — so Guided OAuth works without per-client registration. Dignite Vault Extract deliberately does **not** run Dynamic Client Registration (RFC 7591): it is self-hosted and faces a knowable set of clients, so an open registration endpoint would be pure attack surface. Instead you paste the preset client_id into each client's OAuth settings — every real target supports a manually specified client_id.

#### Configure the preset client

Point your client at `https://<host>/mcp` with **no** token and supply the client_id `Extract_Mcp` (no client secret — it is a public PKCE client):

- **MCP Inspector** — in the OAuth / Authentication settings panel set **Client ID** to `Extract_Mcp`, then run *Guided OAuth*.
- **Claude Desktop / claude.ai custom connector** — in the connector's *Advanced settings* set the **OAuth Client ID** to `Extract_Mcp` (this field exists precisely for servers that don't offer DCR).
- **mcp-remote / Cursor** — set the configured OAuth client id to `Extract_Mcp`.

The browser opens the Dignite Vault Extract login; you sign in, and — because this is a public client with a published client_id — you get an **explicit consent screen** (`ConsentType = Explicit`) before any token is issued. The client then connects automatically.

> The preset client only carries the `Dignite Vault Extract` scope (plus minimal `profile` / `email` identity scopes). Actual data access is still gated by the **logged-in user's** `Extract.Documents` permission — grant it via the admin UI. The auth-code flow logs in a *user*; the client itself holds no data permission, so a user without `Extract.Documents` is denied fail-closed even after a successful login.

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
    "Extract_Mcp": {
      "ClientId": "Extract_Mcp",
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

> Multi-tenancy is currently disabled (`ExtractHostModule.IsMultiTenant = false`), so all access resolves to the host document space. Tenant isolation is still enforced fail-closed in code (explicit `TenantId` predicate), so it stays correct if multi-tenancy is later enabled.

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

Restart Claude Desktop; the `search_extract_documents` tool and `extract://documents/{id}` resources become available.

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

1. **Register Claude Code's callback.** Its OAuth callback path is `http://localhost:<port>/callback`, which the seeded defaults don't include. Add `http://localhost/callback` to the `Extract_Mcp` redirect URIs (the **Registered callbacks** section above shows the override — the `Native` client type relaxes the port, so register it without one), then re-seed with `--migrate-database` and restart the host.

2. **Trust the dev certificate (local testing only).** Claude Code's bundled Node honours neither the system certificate store nor `NODE_EXTRA_CA_CERTS`, so the **Local TLS** note above (which targets system Node) does not apply here. Instead add an `env` block to `.claude/settings.local.json` — Claude Code injects it into every internal process, including the OAuth sub-process:

   ```json
   {
     "env": { "NODE_TLS_REJECT_UNAUTHORIZED": "0" }
   }
   ```

   This disables TLS verification for that process — dev machines only. A host on a real CA-signed certificate needs none of it.

3. **Add the server** (the preset public PKCE client — no secret, no fixed callback port):

   ```powershell
   claude mcp add --transport http extract https://localhost:44348/mcp --client-id Extract_Mcp
   ```

4. **Log in.** Restart Claude Code, run `/mcp`, select **extract** → **Authenticate**, and sign in through the browser. Tokens are stored and refreshed automatically; the `search_extract_documents` tool and `extract://documents/{id}` resources become available.

## Typical flow

1. Client calls `search_extract_documents` with a required `documentTypeCode` (and optionally `lifecycleStatus`, plus zero or more `fieldFilters` — each names a field with a `Value` for equality or a `Min`/`Max` numeric/date range; multiple filters are AND-ed). If the user hasn't named a document type, the client asks first.
2. The tool returns thin rows, each with a `extract://documents/{id}` uri.
3. Client calls `resources/read` on a uri to pull that document's full Markdown.

## Notes & limits

- **One entry point.** The tool is a thin adapter over the same application-service use case as the REST document list (`IDocumentAppService.GetListAsync`): permission assertion, input validation, field-definition resolution, and tenant isolation all run there. The tool only handles transport concerns — tolerant `lifecycleStatus` string parsing, clamping the row count, and `PromptBoundary`-wrapping titles.
- **Result cap.** The search tool returns at most `DocumentConsts.MaxSearchResultCount` (50) rows. The MCP adapter clamps the requested count to this ceiling (an LLM-context safety limit, not a paging window); the REST list endpoint pages normally.
- **`ExtractedFields` search performance.** Field values are stored as first-class rows in a `DocumentExtractedField` table (one row per field, value in a typed column — `StringValue` / `BooleanValue` / `NumberValue` / `DateValue` / `DateTimeValue` — keyed by `(DocumentId, FieldDefinitionId)`). Field-value filtering is plain EF Core LINQ: the query anchors on `Documents` (so tenant + soft-delete global filters apply automatically) and compiles each filter to an `EXISTS` over the child rows with an ordinary typed-column comparison (`=` / range). No SQL Server `JSON_VALUE` / `TRY_CONVERT` / native `json` column — the query is portable across SQL Server, PostgreSQL, MySQL, and SQLite, and ordinary B-tree indexes serve both equality and range (issue #206). The wire-format `ExtractedFields` object on a search result is assembled from these rows on read.
- **Field-value semantics.** Each filter in `fieldFilters` names a field plus either an exact `Value` or an inclusive `Min`/`Max` range; multiple filters are combined with **AND** (every filter must match) and all anchor to the one `documentTypeCode`. Each field's query is dispatched by its declared `FieldDataType`, resolved **server-side** from the `(documentTypeCode, name)` `FieldDefinition` — the caller never supplies the type. `Text`/`Boolean` support equality (`Value`) only; `Number`/`Date`/`DateTime` support equality **or** an inclusive `Min`/`Max` range. Passing a range on a `Text`/`Boolean` field is rejected. Queries use only `=` and range comparisons — **never `LIKE`**. Each field name is resolved server-side against its `FieldDefinition` (unknown names raise a business error) and compiled into a parameterized LINQ column comparison — it is never interpolated into SQL, so there is no raw-SQL injection surface. **Errors are loud, not silent**: a malformed request fails with a corrigible error rather than an empty result, so an AI client can self-correct instead of mistaking a bad query for "no documents." A filter with no value, an over-length value, more than `DocumentConsts.MaxSearchFieldFilters` filters, or field filters without a `documentTypeCode` fail validation; a field not defined on that document type, a range on a `Text`/`Boolean` field, or a value that doesn't parse to the field's type raise a business error. A **valid** filter that simply matches nothing returns an empty list (not an error).
- **Input length caps.** Over-length `documentTypeCode` or per-filter field values (`Value` / `Min` / `Max`) are rejected before any scan, keeping an authorized client from forcing expensive table scans through the AI-facing tool.
- **Untrusted body.** A document's Markdown is wrapped in `<document>` tags when read as a resource. Embedded text is never treated as instructions by Dignite Vault Extract, but consuming clients should still treat document content as untrusted.
- **Single instance.** The Streamable HTTP transport keeps session state in-process. Running multiple host instances behind a load balancer requires session affinity (or a future stateless/distributed-store configuration).
