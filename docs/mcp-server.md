# MCP Server

Paperbase exposes an **MCP (Model Context Protocol) server** as one of its channel exits, alongside REST, EventBus, and Webhook. It lets AI clients (Claude Desktop, Cursor, any MCP client) read Paperbase documents and search them — without writing custom integration code.

> **Paperbase is a channel layer.** The MCP server exposes documents as resources plus a structured **search tool** (metadata + extracted-field values, anchored to a document type). It does **not** do keyword/full-text or semantic / vector retrieval (those belong to a downstream RAG consumer — see CLAUDE.md "OUT of scope"). It is an MCP **server** only; Paperbase never acts as an MCP client.

## What v1 exposes

| MCP primitive | Paperbase mapping |
| --- | --- |
| `resources/read` (template `paperbase://documents/{id}`) | A small system-metadata header (type, lifecycle, language, created-at) followed by the document's Markdown body wrapped in `<document>` tags. The wrapped body is external, untrusted content — the header tells clients to treat it as data, not instructions |
| `tools/call` → `search_paperbase_documents` | Structured search **within a required `documentTypeCode`**: metadata (`lifecycleStatus`) + zero or more `ExtractedFields` field-value filters, all combined with **AND** (each is an equality, or a numeric/date `min`/`max` range). No keyword/full-text search. Returns up to 50 thin rows; each row carries the `paperbase://documents/{id}` uri to read the full document |

The server declares only the bare `resources` capability — **no `subscribe` / `listChanged`**. v1 is pull-only: clients read on demand. Push (resource subscriptions + `notifications/resources/updated` / `list_changed`) is a follow-up increment (see issue #197).

The transport is **Streamable HTTP** at `/mcp`. (The legacy SSE transport is not exposed.)

## Authentication

The `/mcp` endpoint reuses the host's existing **OpenIddict Bearer** auth — the same scheme as the REST API (audience `Paperbase`). There is no separate API-key system in v1.

Every request to `/mcp` requires a valid Bearer token (`RequireAuthorization` on the endpoint). In addition, each tool/resource call performs an explicit server-side permission assertion: the caller must be granted **`Paperbase.Documents`** (`PaperbasePermissions.Documents.Default`). A token without that permission gets an authorization error even though the endpoint accepted the connection (fail-closed, defense in depth).

Obtain a token from the Paperbase auth server (`AuthServer:Authority`) using your normal OAuth flow (e.g. client-credentials for a service client, or an interactive user token), then grant the client/user the `Paperbase.Documents` permission via the admin UI.

> Multi-tenancy is currently disabled (`PaperbaseHostModule.IsMultiTenant = false`), so all access resolves to the host document space. Tenant isolation is still enforced fail-closed in code (explicit `TenantId` predicate), so it stays correct if multi-tenancy is later enabled.

## Connect Claude Desktop

Claude Desktop talks to remote HTTP MCP servers through the `mcp-remote` stdio bridge. In `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "paperbase": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "https://your-paperbase-host/mcp",
        "--header", "Authorization: Bearer ${PAPERBASE_TOKEN}"
      ],
      "env": { "PAPERBASE_TOKEN": "<your-bearer-token>" }
    }
  }
}
```

Restart Claude Desktop; the `search_paperbase_documents` tool and `paperbase://documents/{id}` resources become available.

## Connect Cursor

Cursor reads remote HTTP MCP servers directly. In `.cursor/mcp.json` (project) or the global Cursor MCP settings:

```json
{
  "mcpServers": {
    "paperbase": {
      "url": "https://your-paperbase-host/mcp",
      "headers": { "Authorization": "Bearer <your-bearer-token>" }
    }
  }
}
```

## Typical flow

1. Client calls `search_paperbase_documents` with a required `documentTypeCode` (and optionally `lifecycleStatus`, plus zero or more `fieldFilters` — each names a field with a `Value` for equality or a `Min`/`Max` numeric/date range; multiple filters are AND-ed). If the user hasn't named a document type, the client asks first.
2. The tool returns thin rows, each with a `paperbase://documents/{id}` uri.
3. Client calls `resources/read` on a uri to pull that document's full Markdown.

## Notes & limits

- **One entry point.** The tool is a thin adapter over the same application-service use case as the REST document list (`IDocumentAppService.GetListAsync`): permission assertion, input validation, field-definition resolution, and tenant isolation all run there. The tool only handles transport concerns — tolerant `lifecycleStatus` string parsing, clamping the row count, and `PromptBoundary`-wrapping titles.
- **Result cap.** The search tool returns at most `DocumentConsts.MaxSearchResultCount` (50) rows. The MCP adapter clamps the requested count to this ceiling (an LLM-context safety limit, not a paging window); the REST list endpoint pages normally.
- **`ExtractedFields` search performance.** Field values are stored as first-class rows in a `DocumentExtractedField` table (one row per field, value in a typed column — `StringValue` / `BooleanValue` / `DecimalValue` / `DateValue` / `DateTimeValue` — keyed by `(DocumentId, FieldDefinitionId)`). Field-value filtering is plain EF Core LINQ: the query anchors on `Documents` (so tenant + soft-delete global filters apply automatically) and compiles each filter to an `EXISTS` over the child rows with an ordinary typed-column comparison (`=` / range). No SQL Server `JSON_VALUE` / `TRY_CONVERT` / native `json` column — the query is portable across SQL Server, PostgreSQL, MySQL, and SQLite, and ordinary B-tree indexes serve both equality and range (issue #206). The wire-format `ExtractedFields` object on a search result is assembled from these rows on read.
- **Field-value semantics.** Each filter in `fieldFilters` names a field plus either an exact `Value` or an inclusive `Min`/`Max` range; multiple filters are combined with **AND** (every filter must match) and all anchor to the one `documentTypeCode`. Each field's query is dispatched by its declared `FieldDataType`, resolved **server-side** from the `(documentTypeCode, name)` `FieldDefinition` — the caller never supplies the type. `String`/`Boolean` support equality (`Value`) only; `Number`/`Date`/`DateTime` support equality **or** an inclusive `Min`/`Max` range. Passing a range on a `String`/`Boolean` field is rejected. Queries use only `=` and range comparisons — **never `LIKE`**. Each field name is resolved server-side against its `FieldDefinition` (unknown names raise a business error) and compiled into a parameterized LINQ column comparison — it is never interpolated into SQL, so there is no raw-SQL injection surface. **Errors are loud, not silent**: a malformed request fails with a corrigible error rather than an empty result, so an AI client can self-correct instead of mistaking a bad query for "no documents." A filter with no value, an over-length value, more than `DocumentConsts.MaxSearchFieldFilters` filters, or field filters without a `documentTypeCode` fail validation; a field not defined on that document type, a range on a `String`/`Boolean` field, or a value that doesn't parse to the field's type raise a business error. A **valid** filter that simply matches nothing returns an empty list (not an error).
- **Input length caps.** Over-length `documentTypeCode` or per-filter field values (`Value` / `Min` / `Max`) are rejected before any scan, keeping an authorized client from forcing expensive table scans through the AI-facing tool.
- **Untrusted body.** A document's Markdown is wrapped in `<document>` tags when read as a resource. Embedded text is never treated as instructions by Paperbase, but consuming clients should still treat document content as untrusted.
- **Single instance.** The Streamable HTTP transport keeps session state in-process. Running multiple host instances behind a load balancer requires session affinity (or a future stateless/distributed-store configuration).
