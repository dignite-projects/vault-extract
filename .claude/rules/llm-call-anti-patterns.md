---
description: "Dignite Vault Extract internal LLM call-site anti-patterns: fail-closed security gate / PromptBoundary / compile-time-constant descriptions / MCP schema collection-parameter pitfall"
paths:
  - "**/*Workflow*.cs"
  - "**/Pipelines/**/*.cs"
  - "**/*Extraction*.cs"
  - "**/*Mcp*/**/*.cs"
  - "**/*McpTool*.cs"
  - "**/*Prompt*.cs"
  - "**/*ChatClient*.cs"
  - "**/*VisionLlm*/**/*.cs"
---

# LLM call anti-patterns

This file is referenced by the `maf-workflow-reviewer` agent during PR review to quickly locate typical mistakes at Dignite Vault Extract's internal **LLM call sites** (anti-patterns A / B are security issues, C is an MCP schema correctness issue, D is a cost / availability issue). The rules apply to all LLM entry points:

- Currently landed: `DocumentClassificationWorkflow` / `FieldExtractionWorkflow` + `FieldExtractionEventHandler` (field architecture v2 unifying Host + tenant (mechanism B)) / `DocumentParseBackgroundJob.TryGenerateTitleAsync`
- Future extensions: MCP server tools ([#170](https://github.com/dignite-projects/vault-extract/issues/170)), Webhook-triggered LLM paths, any query path where LLM output influences the parameters

All examples are **pseudocode**, not compilable, illustrating intent only.

---

## Anti-pattern A: a field-extraction Agent attaching AIContextProviders

**Rule source**: `maf-workflow-reviewer.md § 2.10`

**Scope**: all structured field-extraction paths — classification, Host field extraction, tenant field extraction (mechanism B), and any future field-extraction workflow / agent.

### ❌ Wrong

```csharp
// Wrong: attaching a TextSearchProvider (RAG retrieval) to a field-extraction agent
var provider = new TextSearchProvider(
    async (query, ct) => /* fetch chunks from your vector store */,
    options: /* TextSearchProviderOptions */);
var options = new ChatClientAgentOptions
{
    AIContextProviders = [provider],          // ← forbidden
    ChatHistoryProvider = new InMemory...()   // ← forbidden
};
var agent = new ChatClientAgent(_chatClient, options);
var run = await agent.RunAsync<HostFieldExtractionResult>(markdown);
```

**Harm**:

- RAG retrieval injects chunks from other, unrelated documents into the prompt, causing structured fields like "contract amount" and "party A name" to be extracted **from the wrong document** and written into a downstream business aggregate root / the Dignite Vault Extract type-bound field table
- Dignite Vault Extract is a **channel layer**; the input to field extraction should be **only the current document's Markdown** — attaching `AIContextProviders` corrupts the channel philosophy into RAG, violating CLAUDE.md's "OUT of scope"

### ✅ Correct

```csharp
// Correct: only IChatClient + system instructions, structured output via RunAsync<T>
var agent = new ChatClientAgent(
    _chatClient,
    instructions: HostFieldExtractionInstructions.SystemPrompt);
var run = await agent.RunAsync<HostFieldExtractionResult>(markdown);
return run.Result ?? new HostFieldExtractionResult();
```

Or, like the current `FieldExtractionWorkflow`, call `IChatClient.GetResponseAsync` directly, bypassing the agent wrapper:

```csharp
// Correct: construct the ChatMessage list directly, no AIContextProvider interference
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt + "\n\n" + PromptBoundary.BoundaryRule),
    new(ChatRole.User, PromptBoundary.WrapDocument(truncated))
};
var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
```

**Reference implementations**:
- `core/src/Dignite.Vault.Extract.Application/Documents/Pipelines/Classification/DocumentClassificationWorkflow.cs`
- `core/src/Dignite.Vault.Extract.Application/Documents/Pipelines/FieldExtraction/FieldExtractionWorkflow.cs`

---

## Anti-pattern B: an LLM-triggered query path without a fail-closed security gate

**Rule source**: `maf-workflow-reviewer.md § 2.9`

**Background**: Dignite Vault Extract Core's current LLM paths are all **plain text input → structured output** (classification, field extraction, title generation), with no DB query parameterized by LLM output. But the upcoming #170 MCP server will let an LLM trigger document retrieval / metadata queries via a tool interface. **Any query path triggered by an LLM, or whose parameters are influenced by LLM output**, must have a fail-closed security gate — the `[Authorize]` at the HTTP boundary does not cover this reflection / tool-dispatch path; the security assertion inside the script / tool method body is the only line of defense.

**Scope**:

- Future MCP server tool methods (#170)
- Future Webhook handler paths that trigger LLM tool calls
- Any code point where an LLM obtains query parameters that reach `IRepository.GetQueryableAsync()` / `IAsyncQueryableExecuter.ToListAsync()`

### ❌ Wrong 1: relying on the AppService's `[Authorize]` / not asserting permission

```csharp
[McpTool("search-documents")]
public async Task<string> SearchAsync(string documentTypeCode, string fieldName, string fieldValue, IServiceProvider sp, CancellationToken ct)
{
    // Wrong: directly serializing and returning the AppService method result
    // IDocumentAppService's [Authorize(Documents.Default)] does not fire on an LLM reflection call
    var appService = sp.GetRequiredService<IDocumentAppService>();
    var result = await appService.GetListAsync(new GetDocumentListInput { DocumentTypeCode = documentTypeCode });
    return JsonSerializer.Serialize(result);
}
```

**Harm**: a client holding only MCP endpoint access credentials can, via natural language ("help me find Zhang San's contract"), obtain documents it has no right to access. The LLM is an unwitting "privilege-escalation channel".

### ❌ Wrong 2: disabling the tenant filter on an LLM-triggered path / mapping the endpoint outside the multi-tenancy middleware

```csharp
public async Task<string> SearchAsync(string keyword)
{
    await _authService.CheckAsync(DocumentPermissions.Default);
    // Wrong: disabling the IMultiTenant global filter on an LLM-reachable path
    using (DataFilter.Disable<IMultiTenant>())
    {
        var q = await _repo.GetQueryableAsync();   // ← no longer filtered by tenant — cross-tenant leak
        return JsonSerializer.Serialize(await _executer.ToListAsync(q.Where(...).Take(20)));
    }
}
```

**Clarification**: ABP's `IMultiTenant` global query filter **is** the framework-level tenant boundary — it is driven by the authenticated principal (the token's tenant claim, resolved by `CurrentUserTenantResolveContributor` with the highest priority, not forgeable via the `__tenant` header), in effect by default for all queries, and `FromSqlRaw` is equally bound once EF Core wraps it into a subquery. So **the normal path relies on it and need not hand-write a `TenantId` predicate** — hand-writing is merely redundant, and when the caller deliberately disables the filter it silently clamps the result to the ambient tenant against the caller's intent, while permanently stripping the legitimate cross-tenant retrieval capability.

**The real anti-pattern** is the reverse — piercing this boundary: `DataFilter.Disable<IMultiTenant>()` / `IgnoreQueryFilters()` on an LLM-triggered path (as above), or mapping the MCP / Webhook endpoint outside `UseMultiTenancy()` (tenant cannot be resolved → everything runs as host data).

### ❌ Wrong 3: an unbounded result set

```csharp
var matches = await _executer.ToListAsync(q.Where(d => d.Title.Contains(keyword)));
return JsonSerializer.Serialize(matches);   // ← 5000 hits all returned
```

**Harm**:

- A single tool call blows up the LLM context window, degrading subsequent turns
- An attacker crafts memory pressure or a cost attack via a broad keyword (e.g. `""`)

### ❌ Wrong 4: concatenating user input into the tool description / instructions

```csharp
public override McpToolDescriptor Descriptor { get; } = new(
    "search-documents",
    $"Search documents belonging to user {someUserName}. ...");   // ← forbidden: constructor parameter concatenated at runtime

// or:
protected override string Instructions => $"You serve user {dynamicValue}. ...";
```

**Harm**: the description / instructions text is part of the LLM's decision context. If it contains user-controlled strings (nicknames, signatures, document names), it can serve as a prompt-injection vector. Both Descriptor text and instructions must be **compile-time constants** or pure static literals.

### ❌ Wrong 5: running raw SQL

```csharp
public async Task<string> ReportAsync(string whereClause)
{
    var sql = $"SELECT * FROM Documents WHERE {whereClause}";   // ← LLM concatenates SQL
    return await _dbContext.Database.SqlQueryRaw<...>(sql).ToListAsync();
}
```

**Harm**: a SQL-injection surface + bypassing the ABP permission / audit / soft-delete / tenant-filter layers. Even LLM-generated SQL that looks controllable is within the attack surface (prompt injection can perfectly well induce the LLM to write `WHERE 1=1` or `; DROP TABLE`).

### ✅ Correct implementation essentials

Each LLM-triggered query point (whether an MCP tool, a Webhook handler, or any similar entry) satisfies the following in order:

```csharp
[McpTool("search-documents")]
[Description("Search Dignite Vault Extract documents by structured criteria.")]   // ← compile-time constant
private static async Task<string> SearchAsync(
    string? keyword,
    [Description("ISO 4217 currency code (optional).")] string? currency,
    IServiceProvider serviceProvider,
    CancellationToken cancellationToken = default)
{
    // 1. Explicit permission assertion — fail closed
    var authSvc = serviceProvider.GetRequiredService<IAuthorizationService>();
    await authSvc.CheckAsync(ExtractPermissions.Documents.Default);

    // 2. Tenant isolation — left to ABP's IMultiTenant global filter; do not hand-write a TenantId predicate,
    //    and never Disable<IMultiTenant>() / IgnoreQueryFilters() here
    var repo = serviceProvider.GetRequiredService<IDocumentRepository>();
    var q = await repo.GetQueryableAsync();   // ← automatically filtered by the resolved tenant

    // 3. Business filter + mandatory Take(N)
    var executer = serviceProvider.GetRequiredService<IAsyncQueryableExecuter>();
    var rows = await executer.ToListAsync(
        q.Where(d => keyword == null || d.Title.Contains(keyword))
         .OrderByDescending(d => d.CreationTime)
         .Take(MaxResultRows),
        cancellationToken);

    // 4. User-derived free-text fields must be wrapped by PromptBoundary.WrapField(...)
    return JsonSerializer.Serialize(new
    {
        rows = rows.Select(r => new
        {
            r.Id,
            r.CreationTime,
            title = PromptBoundary.WrapField(r.Title),            // ← wrapped
            documentTypeCode = r.DocumentTypeCode                 // system field, no wrap needed
        })
    });
}
```

**Key points recap**:

1. **`[Authorize]` is not enough** — you must explicitly `CheckAsync(Permission)` in the method body
2. **Tenant isolation is left to the framework filter** — ABP's `IMultiTenant` global filter is the tenant boundary (driven by the authenticated principal's tenant claim, including the `FromSqlRaw` path); do not hand-write a `TenantId` predicate, just ensure you don't `Disable<IMultiTenant>()` / `IgnoreQueryFilters()` on the LLM path
3. **Must `Take(N)`** — a hard cap on the single tool call's result set, preventing prompt-injection-induced broad queries
4. **description / instructions must be compile-time constants** — concatenating user strings is forbidden
5. **No raw SQL** — LLM-concatenated SQL is within the attack surface
6. **PromptBoundary** — user-derived free-text fields in the return value (title, partyName, summary, etc.) must be wrapped by `PromptBoundary.WrapField(...)`

---

## Common security conventions (apply to all LLM paths)

The anti-patterns above are concretizations of the 4 bullets in CLAUDE.md's "## Security conventions" section:

- **Fail-closed security assertion** — permission + tenant + cap + no raw SQL
- **PromptBoundary** — user-derived free text must be wrapped before entering a prompt / LLM-facing output
- **Description / Instructions are compile-time constants** — concatenating user strings at runtime is forbidden
- **Multi-tenancy isolation** — rely on ABP's `IMultiTenant` global filter (framework-level boundary, including `FromSqlRaw`); do not hand-write predicates; the only discipline is not to disable the filter on LLM paths
- **Bounded payloads** — every text crossing an LLM boundary has a ceiling (see anti-pattern D). Where the tail is load-bearing the ceiling **gates** the call; otherwise it truncates surrogate-safely and announces the cut. `Take(N)` bounds rows, not bytes

When adding any new LLM call site, self-review against these 5 points; the `maf-workflow-reviewer` agent also checks against this list during PR review.

---

## Anti-pattern C: an LLM-facing collection parameter on an MCP tool / resource using a collection-interface / array type

**Rule source**: empirical to this repo (`DocumentSearchTool.fieldFilters` once silently broke). **This is a schema correctness issue, not a security issue** — but it is likewise a "typical mistake at an LLM call site", hence its inclusion here.

**Background**: ABP uses the Autofac container. Autofac treats **all collection relationship types** (`IEnumerable<T>` / `IReadOnlyList<T>` / `IList<T>` / `ICollection<T>` / `IReadOnlyCollection<T>` / `T[]`) as implicitly resolvable services — `IServiceProviderIsService.IsService(typeof(IReadOnlyList<Foo>))` returns `true`. And the MCP SDK's (ModelContextProtocol 1.3.0) parameter-binding rule is: **a parameter whose `IsService(paramType) == true` is `ExcludeFromSchema`** — removed from the inputSchema the LLM sees, and instead injected from DI at runtime.

**Consequence**: an LLM-facing parameter declared with a collection interface / array **silently disappears** — the LLM cannot see it and never passes a value; the tool can still be called (injected an empty collection at runtime), so the functionality that parameter backs is **permanently broken with no error**. Scalar parameters (`string` / `int?`, `IsService=false`) are unaffected.

### ❌ Wrong

```csharp
[McpServerTool(Name = "search_documents")]
public static async Task<...> SearchAsync(
    string documentTypeCode,                          // ✅ string → IsService=false → in schema
    IReadOnlyList<FieldFilter>? fieldFilters = null,  // ❌ collection interface → Autofac IsService=true → excluded from schema
    FieldFilter[]? more = null)                       // ❌ array also excluded (counterintuitive: T[] is also a collection relationship type)
```

### ✅ Correct

```csharp
[McpServerTool(Name = "search_documents")]
public static async Task<...> SearchAsync(
    string documentTypeCode,
    // Must use a concrete List<T> (IsService=false); collection interfaces / arrays are silently excluded.
    List<FieldFilter>? fieldFilters = null)
```

**Guard**: use a test that **actually goes through MCP schema generation** — `McpServerTool.Create(method, target, new McpServerToolCreateOptions { Services = autofacServiceProvider })`, asserting that the `ProtocolTool.InputSchema`'s `properties` contains the parameter. **A unit test that calls the C# method directly cannot catch this bug — it bypasses schema generation**. See `DocumentSearchTool_Tests.Mcp_input_schema_exposes_fieldFilters_and_all_llm_parameters`.

**Note**: the MS DI container returns `IsService=true` only for `IEnumerable<T>`, and `false` for other collection interfaces — this pitfall is Autofac-specific, so repro / guard tests must run under the Autofac container (the test base class already calls `UseAutofac()`).

---

## Anti-pattern D: an unbounded payload entering a prompt, or leaving on an LLM-facing egress

**Rule source**: [#491](https://github.com/dignite-projects/vault-extract/issues/491). **This is a cost / availability issue**, and the one anti-pattern here that a correct implementation of B can still commit.

**Do not confuse it with B point 3.** B caps *how many rows* a query returns. D caps *how large one payload is*. A tool can honour `Take(20)` perfectly and still hand the model a 40 MB document body. The two bounds are orthogonal and you need both.

**Scope**: any text crossing an LLM boundary — a document body or field value entering a prompt, and any body returned to an MCP client or other LLM-facing consumer.

### ❌ Wrong 1: feeding a whole document into a prompt with no ceiling

```csharp
// Wrong: markdown is bounded only by the upload size limit, which is not a Markdown bound at all
// (a text-dense DOCX/XLSX is a ZIP; its extracted body is routinely 10x the uploaded bytes).
var messages = new List<ChatMessage>
{
    new(ChatRole.System, SystemInstructions),
    new(ChatRole.User, PromptBoundary.WrapDocument(markdown))   // ← unbounded prompt-token cost
};
await _chatClient.GetResponseAsync(messages, options, ct);
```

**Harm**:

- Unbounded per-document token spend, on the **host's** bill; under multi-tenancy a tenant chooses the input and the host pays
- `PromptBoundary.Encode` (a `Replace`) + `WrapDocument` interpolation + request serialization each materialize another full copy — roughly 4× the body in UTF-16, all on the large object heap, multiplied by concurrent jobs
- Bulk re-processing sweeps replay the cost across every document of a type

### ❌ Wrong 2: letting the provider's context-window error fault the job

```csharp
catch (Exception ex)
{
    await FailRunAsync(documentId, runId, ex.Message, pipeline);
    throw;   // ← back to the ABP job store, which re-sends the same oversized body on every retry
}
```

An oversized body is a **permanent** property of the document, not a transient fault. Rethrowing converts one bad document into N identical failures and never reaches a terminal state.

### ❌ Wrong 3: returning an uncapped body to an MCP client

```csharp
return new DocumentDetailResult
{
    Markdown = PromptBoundary.WrapDocument(document.Markdown ?? string.Empty)   // ← eats the client's context window
};
```

### ❌ Wrong 4: silently truncating where the tail is load-bearing

```csharp
// Wrong for field extraction: a contract amount / invoice number can sit anywhere in the document.
// Tail truncation disguises "missed extraction" as "successful extraction" — strictly worse than failing.
var truncated = markdown[..MaxChars];
```

### ✅ Correct: decide gate vs. truncate by whether the tail is load-bearing

Every LLM-facing path picks exactly one, and it follows from the semantics of the call, not from convenience:

| Path | Tail load-bearing? | Bound |
|---|---|---|
| `DocumentClassificationWorkflow` | no — the leading semantics classify | truncate at `MaxTextLengthPerExtraction` |
| `DocumentParseBackgroundJob.TryGenerateTitleAsync` | no | truncate at `MaxTitleGenerationMarkdownLength` |
| `CabinetSuggestionWorkflow` | no | truncate (reuses `MaxTextLengthPerExtraction`) |
| `DocumentSegmentationJob` | **yes** — a boundary can be anywhere | **gate** at `MaxSegmentationMarkdownLength` → `SegmentationIncomplete` |
| `FieldExtractionService` | **yes** — a field can be anywhere | **gate** at `MaxFieldExtractionMarkdownLength` → `FieldExtractionIncomplete` |
| MCP `get_document` / `documents/{id}` | no — the client can re-read | truncate at `VaultExtractMcpConsts.MaxDocumentMarkdownChars` + announce |

```csharp
// Gate: no call at all above the ceiling, a review signal instead, and a TERMINAL outcome (never a rethrow).
if (markdown.Length > _behaviorOptions.MaxFieldExtractionMarkdownLength)
{
    return await DeclineOversizedAsync(documentId, tenantId, documentTypeId, markdown.Length);
}

// Truncate: surrogate-safe, and announced whenever a consumer could mistake a prefix for the whole.
var clipped = TextTruncator.AtCharBoundary(body, VaultExtractMcpConsts.MaxDocumentMarkdownChars);
return new DocumentDetailResult
{
    Markdown = PromptBoundary.WrapDocument(clipped),   // truncate first, wrap second: the tags must survive the cut
    MarkdownTruncated = clipped.Length < body.Length,
    MarkdownTotalChars = body.Length
};
```

**Key points recap**:

1. **`Take(N)` is not enough** — it bounds rows, not bytes; a single row's payload needs its own ceiling
2. **Never truncate a load-bearing tail** — gate the call instead, and raise a review signal so the miss is visible
3. **A gate is a terminal outcome** — never rethrow an oversized body into the background-job retry loop
4. **Never cut with a raw range slice** — `text[..n]` can split a surrogate pair; use `TextTruncator.AtCharBoundary`
5. **Announce every truncation** — `Truncated` / `markdownTruncated` + a total, so an LLM cannot mistake a prefix for the whole
6. **Prompt ceilings are configuration, egress ceilings are `const`** — a host tunes its own token budget (`VaultExtractBehaviorOptions`), but the safety boundary of an LLM-facing egress must not be widenable at runtime (`VaultExtractMcpConsts`)
