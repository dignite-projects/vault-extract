---
description: "Dignite Vault Extract internal LLM call-site anti-patterns: fail-closed security gate / PromptBoundary / compile-time-constant descriptions / MCP schema collection-parameter pitfall"
paths:
  - "core/src/**/*Workflow*.cs"
  - "core/src/**/Pipelines/**/*.cs"
  - "core/src/**/*Extraction*.cs"
  - "core/src/**/*Mcp*/**/*.cs"
  - "core/src/**/*Prompt*.cs"
  - "core/src/**/*ChatClient*.cs"
  - "core/src/**/*VisionLlm*/**/*.cs"
---

# LLM call anti-patterns

Typical mistakes at Dignite Vault Extract's internal **LLM call sites**. A / B / E are security issues, C is an MCP schema correctness issue, D is a cost / availability issue. They apply to every LLM entry point: classification, field extraction (`FieldExtractionWorkflow` + `FieldExtractionService`), title generation, segmentation, cabinet / slug / field-definition draft suggestions, VisionLlm OCR, the MCP tools / resources (`Dignite.Vault.Extract.Mcp`), any future path (e.g. Webhook-triggered LLM work), and any query path where LLM output influences the parameters. Examples are pseudocode.

The five conventions in CLAUDE.md "Security conventions" (fail-closed assertion, PromptBoundary, compile-time-constant descriptions, multi-tenancy isolation, bounded payloads) are what these anti-patterns make concrete. Self-review a new LLM call site against those five.

---

## Anti-pattern A: a field-extraction agent attaching AIContextProviders

**Scope**: every structured extraction path — classification, Host / tenant field extraction (mechanism B), and any future one.

The input to field extraction is **only the current document's Markdown**. Attaching a `TextSearchProvider` (RAG retrieval) via `ChatClientAgentOptions.AIContextProviders`, or a `ChatHistoryProvider`, injects chunks from other, unrelated documents, so "contract amount" / "party A name" get extracted **from the wrong document** and written into a type-bound field. It also turns the channel layer into RAG, violating CLAUDE.md "OUT of scope".

Correct: `IChatClient` + system instructions only, structured output via `RunAsync<T>`, or — as `FieldExtractionWorkflow` does — build the `ChatMessage` list yourself and call `IChatClient.GetResponseAsync`:

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt + "\n\n" + PromptBoundary.BoundaryRule),
    new(ChatRole.User, PromptBoundary.WrapDocument(truncated))
};
var response = await _chatClient.GetResponseAsync(messages, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, ct);
```

Reference implementations: `DocumentClassificationWorkflow.cs`, `FieldExtractionWorkflow.cs` (under `core/src/Dignite.Vault.Extract.Application/Documents/Pipelines/`).

---

## Anti-pattern B: an LLM-triggered query path without a fail-closed security gate

**Why**: `[Authorize]` at the HTTP boundary does not cover the reflection / tool-dispatch path — the assertion inside the tool method body is the only line of defense. **Any query path triggered by an LLM, or whose parameters are influenced by LLM output**, needs it. Scope: MCP tools / resources, any Webhook handler that triggers LLM tool calls, any code where an LLM obtains parameters that reach `IRepository.GetQueryableAsync()` / `IAsyncQueryableExecuter`.

Five ways to get it wrong:

1. **Relying on the AppService's `[Authorize]` / asserting nothing.** A client holding only MCP endpoint credentials can ask in natural language for documents it has no right to; the LLM becomes an unwitting privilege-escalation channel.
2. **Piercing the tenant boundary.** `DataFilter.Disable<IMultiTenant>()` / `IgnoreQueryFilters()` on an LLM-reachable path is a cross-tenant leak, as is mapping the MCP / Webhook endpoint outside `UseMultiTenancy()` (tenant cannot be resolved → everything runs as host data). Note the framework **is** the boundary: ABP's `IMultiTenant` global filter is driven by the authenticated principal (the token's tenant claim via `CurrentUserTenantResolveContributor`, not forgeable through the `__tenant` header) and also binds `FromSqlRaw` once EF Core wraps it in a subquery. So the normal path needs **no hand-written `TenantId` predicate** — writing one is redundant, and if the filter was deliberately disabled it silently clamps the result to the ambient tenant against the caller's intent.
3. **An unbounded result set.** A broad keyword (even `""`) returns thousands of rows: it blows up the model's context window and is a memory-pressure / cost attack. Always `Take(N)`.
4. **Concatenating user input into a tool description / instructions** (`$"Search documents belonging to {userName}"`). The text is part of the model's decision context; a user-controlled string (nickname, signature, document name) is a prompt-injection vector. Descriptor text and instructions are **compile-time constants** or pure static literals.
5. **Raw SQL** assembled from LLM output: an injection surface that also bypasses ABP's permission / audit / soft-delete / tenant-filter layers. Prompt injection can induce `WHERE 1=1` or `; DROP TABLE` from an LLM that "looks controllable".

Correct essentials, in order — (1) explicit permission assertion in the method body (fail closed); (2) tenant isolation left to the global filter; (3) business filter + mandatory `Take(N)`; (4) user-derived free text wrapped with `PromptBoundary.WrapField(...)` on the way out; system fields need no wrap.

```csharp
[McpServerTool(Name = "search_documents")]
[Description("Search Dignite Vault Extract documents by structured criteria.")]   // compile-time constant
private static async Task<string> SearchAsync(string? keyword, IServiceProvider sp, CancellationToken ct = default)
{
    // 1. explicit assertion, fail closed (in this repo the tools delegate to an application-service use case
    //    whose body performs it; for the documents domain that is DocumentAccessChecker, see authorization.md)
    // 2. tenant: the ambient IMultiTenant filter — never Disable<IMultiTenant>() / IgnoreQueryFilters() here
    var q = await sp.GetRequiredService<IDocumentRepository>().GetQueryableAsync();
    // 3. business filter + mandatory cap
    var rows = await sp.GetRequiredService<IAsyncQueryableExecuter>().ToListAsync(
        q.Where(d => keyword == null || d.Title.Contains(keyword)).OrderByDescending(d => d.CreationTime).Take(MaxResultRows), ct);
    // 4. user-derived free text is wrapped
    return JsonSerializer.Serialize(rows.Select(r => new { r.Id, title = PromptBoundary.WrapField(r.Title) }));
}
```

---

## Anti-pattern E: switching multi-tenancy context before the authorization check

**Source**: [#524](https://github.com/dignite-projects/vault-extract/issues/524) (found reviewing #519, explicit-tenant MCP support in `McpTenantScope`). **Scope**: any path that both accepts a caller-supplied tenant id and calls `ICurrentTenant.Change(...)` to scope a query to it — the MCP `tenantId` parameter / URI-segment paths.

**Not the same mistake as B.** B is a *missing* check; here the check runs but is evaluated against the wrong tenant, because it runs *after* the switch. ABP's role-based `PermissionGrant` rows key on the role **name** plus the ambient `TenantId`, so a caller whose token carries a role name that is also granted in the target tenant (e.g. every tenant's seeded `admin`) passes `CheckPolicyAsync` there, though nothing ties that caller to that tenant. The switch itself is legitimate; it silently moves the authorization boundary with it.

Correct (shipped in #524): gate the switch itself, **before** it happens, with checks that depend only on the caller's real identity. `McpTenantScope.ResolveAsync` / `ResolveRequiredAsync` run the admission checks and return the tenant id **without** touching `ICurrentTenant`; the switch is a separate synchronous `Enter` call. The split is required, not stylistic — see the `McpTenantScope` type doc for the `AsyncLocal` reason a single awaited "resolve and switch" method would silently scope nothing for the caller:

```csharp
var explicitTenantId = await McpTenantScope.ResolveAsync(tenantId, serviceProvider);  // ResolveRequiredAsync for a mandatory {tenantId} uri segment
using var tenantScope = McpTenantScope.Enter(explicitTenantId, serviceProvider);      // the actual ICurrentTenant.Change, in this method's own frame
var result = await documentAppService.GetListAsync(input);
```

Admission order: `VaultExtractMcpOptions.AllowExplicitTenantScope` defaults to `false` (whole capability is opt-in per deployment) → `IMcpTenantAccessValidator.IsAllowedAsync` defaults to deny-all (`DenyAllMcpTenantAccessValidator`, registered by one explicit `TryAddTransient` so a deployment replaces it outright with `context.Services.Replace(...)`) → only then `ITenantStore` existence / active validation. Never before the validator: otherwise a denied caller could tell "denied" from "unknown tenant" and enumerate real tenant ids.

---

## Anti-pattern C: an LLM-facing collection parameter typed as a collection interface / array

**A schema correctness issue, not a security one** (`DocumentSearchTool.fieldFilters` once silently broke). ABP uses Autofac, which treats every collection relationship type (`IEnumerable<T>`, `IReadOnlyList<T>`, `IList<T>`, `ICollection<T>`, `IReadOnlyCollection<T>`, `T[]`) as an implicitly resolvable service — `IServiceProviderIsService.IsService(...)` returns `true`. The MCP SDK (ModelContextProtocol 1.3.0) excludes any parameter with `IsService == true` from the inputSchema and injects it from DI. So the parameter **silently disappears** for the LLM, the tool stays callable (an empty collection is injected), and the feature it backs is permanently broken with no error. Scalars (`string`, `int?`) are unaffected.

```csharp
[McpServerTool(Name = "search_documents")]
public static async Task<...> SearchAsync(
    string documentTypeCode,                          // ok: IsService=false
    // ❌ IReadOnlyList<FieldFilter>? / FieldFilter[]? are excluded from the schema (T[] too — counterintuitive)
    List<FieldFilter>? fieldFilters = null)           // ✅ a concrete List<T> stays in the schema
```

**Guard**: a test that actually goes through schema generation — `McpServerTool.Create(method, target, new McpServerToolCreateOptions { Services = autofacServiceProvider })`, asserting `ProtocolTool.InputSchema.properties` contains the parameter. A test that calls the C# method directly bypasses schema generation and cannot catch this; see `DocumentSearchTool_Tests.Mcp_input_schema_exposes_fieldFilters_and_all_llm_parameters`. The MS DI container returns `IsService=true` only for `IEnumerable<T>`, so the repro must run under Autofac (the test base already calls `UseAutofac()`).

---

## Anti-pattern D: an unbounded payload entering a prompt, or leaving on an LLM-facing egress

**Source**: [#491](https://github.com/dignite-projects/vault-extract/issues/491). A cost / availability issue that a correct implementation of B can still commit: B caps *how many rows* a query returns, D caps *how large one payload is*. A tool can honour `Take(20)` and still hand the model a 40 MB body. Scope: any text crossing an LLM boundary — a document body or field value entering a prompt, and any body returned to an MCP client.

Four ways to get it wrong:

1. **Feeding a whole document into a prompt with no ceiling.** Markdown is bounded only by the upload limit, which is no Markdown bound (a text-dense DOCX/XLSX is a ZIP; its body is routinely 10× the uploaded bytes). Unbounded token spend lands on the **host's** bill — under multi-tenancy a tenant chooses the input; `PromptBoundary.Encode` + `WrapDocument` + request serialization each materialize another full copy (~4× the body in UTF-16, on the LOH, times concurrent jobs); bulk re-processing replays the cost across a whole type.
2. **Letting the provider's context-window error fault the job** (`catch { FailRunAsync(...); throw; }`). An oversized body is a **permanent** property of the document; rethrowing hands it back to the ABP job store, which re-sends the same body on every retry — one bad document becomes N identical failures and never reaches a terminal state.
3. **Returning an uncapped body to an MCP client** (`Markdown = PromptBoundary.WrapDocument(document.Markdown ?? "")`).
4. **Silently truncating where the tail is load-bearing** (`markdown[..MaxChars]` for field extraction). An amount or invoice number can sit anywhere; tail truncation disguises "missed extraction" as "successful extraction" — strictly worse than failing.

Every LLM-facing path picks exactly one bound, following the semantics of the call:

| Path | Tail load-bearing? | Bound |
|---|---|---|
| `DocumentClassificationWorkflow` | no — the leading semantics classify | truncate at `MaxTextLengthPerExtraction` |
| `DocumentParseBackgroundJob.TryGenerateTitleAsync` | no | truncate at `MaxTitleGenerationMarkdownLength` |
| `CabinetSuggestionWorkflow` | no | truncate (reuses `MaxTextLengthPerExtraction`) |
| `DocumentSegmentationJob` | **yes** — a boundary can be anywhere | **gate** at `MaxSegmentationMarkdownLength` → `SegmentationIncomplete` |
| `FieldExtractionService` | **yes** — a field can be anywhere | **gate** at `MaxFieldExtractionMarkdownLength` → `FieldExtractionIncomplete` |
| `FieldExtractionWorkflow` field schema (`Σ FieldDefinition.Prompt`) | **yes** — every field instruction is load-bearing | **reject the configuration write** at the per-type `MaxFieldSchemaPromptLength`; assert again at the call site |
| MCP `get_document` / `documents/{id}` | no — the client can re-read | truncate at `VaultExtractMcpConsts.MaxDocumentMarkdownChars` + announce |

```csharp
// Gate: no call at all above the ceiling, a review signal instead, and a TERMINAL outcome (never a rethrow).
if (markdown.Length > _behaviorOptions.MaxFieldExtractionMarkdownLength)
    return await DeclineOversizedAsync(documentId, tenantId, documentTypeId, markdown.Length);

// Truncate: surrogate-safe, and announced whenever a consumer could mistake a prefix for the whole.
var clipped = TextTruncator.AtCharBoundary(body, VaultExtractMcpConsts.MaxDocumentMarkdownChars);
return new DocumentDetailResult
{
    Markdown = PromptBoundary.WrapDocument(clipped),   // truncate first, wrap second: the tags must survive the cut
    MarkdownTruncated = clipped.Length < body.Length,
    MarkdownTotalChars = body.Length
};
```

Recap (the general form is CLAUDE.md "Bounded payloads"): never truncate a load-bearing tail — gate it and raise a review signal; a gate is terminal, never a rethrow; never cut with a raw `text[..n]` (it can split a surrogate pair — use `TextTruncator.AtCharBoundary`); announce every truncation (`Truncated` / `markdownTruncated` + a total); prompt ceilings are host configuration (`VaultExtractBehaviorOptions`) but egress ceilings are `const` (`VaultExtractMcpConsts`) so the safety boundary cannot be widened at runtime; reject deterministic schema overflow at **configuration write time** (an oversized field schema affects every document of the type and operators cannot edit admin-owned definitions) — enforce the per-type total on create / update / restore / pack import, the workflow assertion is only defense in depth.
