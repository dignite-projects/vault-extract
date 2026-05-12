# Chat

Paperbase exposes a conversational endpoint that lets users ask questions over their document corpus. The chat runs as a MAF `ChatClientAgent` with retrieval-augmented generation: each turn pulls relevant chunks from the [knowledge index](knowledge-index.md), feeds them into the prompt, and returns a grounded answer with citations.

This page covers the chat as a *feature* — what it does, how to tune it, and what knobs are safe to flip. For end-to-end HTTP request/response shapes (idempotency, retry, error handling), see [chat-client.md](chat-client.md).

## What it can do

- **Anchor-driven, not scope-locked.** A conversation may carry a `documentId` — the document the user opened when starting the chat. This is treated as a **soft anchor** in the system prompt (`id` + `documentTypeCode` only — never the title), not a retrieval constraint. The model is free to (and encouraged to) search across other documents and types, follow `DocumentRelation` edges, and reconcile across the corpus. A conversation without `documentId` behaves the same way, just without the anchor hint.
- **Citations.** Every answer carries the chunk(s) that grounded it. The agent prompt enforces `[chunk N]` citations and the result is post-processed into a structured `citations` array (document id, chunk index, snippet, source name). Citations from multiple `search_paperbase_documents` invocations within one turn are **appended and de-duplicated** — never overwritten — and capped at `MaxCapturedCitations`.
- **Tool calling.** The agent always sees the same flat toolset (built-in tools + every business module's contributor tools). See the [Tools](#tools) section below for the catalog and the fail-closed contract every tool follows.
- **Streaming.** The HTTP API exposes both a buffered `POST .../messages` and a Server-Sent-Events `POST .../messages/stream` endpoint. The streaming endpoint emits `PartialText`, `ToolCallStarted`, `ToolCallCompleted`, and a terminal `Done` (or `Error`) delta — see [client guide → Streaming](chat-client.md#3-stream-a-message-server-sent-events) for the protocol.
- **Idempotent turns.** The client generates a `clientTurnId` per turn; replays with the same id never re-invoke the model. Idempotency applies to both the buffered and streaming endpoints.
- **Optional LLM rerank.** Off by default. When enabled, retrieval recall is expanded `RecallExpandFactor`× and the chat model rescues the most relevant `TopK` before the answer prompt.

## Permissions

| Permission | Grants |
|---|---|
| `Paperbase.Chat` | Read own conversations and messages (default) |
| `Paperbase.Chat.Create` | Create a new conversation |
| `Paperbase.Chat.SendMessage` | Send a message in an existing conversation |
| `Paperbase.Chat.Delete` | Delete an owned conversation |

A user holding only `Paperbase.Chat` can read but not act. Tool contributors enforce their own per-feature permissions on top of this — for example `search_contracts` requires `Contracts.Default` even though the chat permission is already held.

## Configuration

Chat-related knobs live in `PaperbaseAIBehavior` alongside the other Application-layer behavior settings (see [ai-provider.md](ai-provider.md) for the full split between `PaperbaseAI` provider wiring and `PaperbaseAIBehavior` behavior knobs).

```json
"PaperbaseAIBehavior": {
  "EnableLlmRerank": false,
  "RecallExpandFactor": 4,
  "ChatTopK": 5,
  "ChatMinScore": 0.45,
  "MaxCapturedCitations": 50,
  "MaxToolsPerTurn": 0
}
```

| Key | Default | Description |
| --- | --- | --- |
| `EnableLlmRerank` | `false` | When enabled, document chat retrieves an expanded candidate set, asks the chat model to rerank chunks by question relevance, and injects only the final `TopK` into the answer prompt. Off by default to conserve tokens; enable when retrieval quality is the bottleneck (often in mixed-language corpora). |
| `RecallExpandFactor` | `4` | Multiplier applied to `ChatTopK` (or `PaperbaseKnowledgeIndex:DefaultTopK`) before LLM rerank. With the defaults `topK=5` × `4` = 20 candidates rescored. |
| `ChatTopK` | `5` | Default top-K passed to `search_paperbase_documents` when the model does not specify it. The model can override per call (e.g. raise to 10–15 for cross-document reconciliation). |
| `ChatMinScore` | `0.45` | Default normalized cosine threshold for document chat RAG searches when the model does not specify `minScore`. Intentionally lower than `PaperbaseKnowledgeIndex:MinScore` to improve recall for cross-language questions and proper-noun lookups. |
| `MaxCapturedCitations` | `50` | Hard upper bound on the number of distinct citations a single turn may accumulate across all `search_paperbase_documents` calls. When the cap is hit, additional results are dropped and `CitationsTrimmed = true` is recorded on the audit row. Defends against prompt-injection-driven citation bombs. |
| `MaxToolsPerTurn` | `0` (unlimited) | Soft cap on the number of contributor tools exposed to the agent per turn. `0` means no cap. When the cap is exceeded, the dispatcher prefers tools whose contributor `DocumentTypeCode` matches the conversation anchor and trims the rest, recording `ToolsTrimmed = true`. Leave at `0` until business modules genuinely outgrow the model's tool-list comprehension. |

The agent uses `ChatToolMode.Auto` — the model picks when (and with what `documentIds` / `documentTypeCode` / `topK` / `minScore`) to invoke each tool. There is no operator switch for "always retrieve before answering" — see *When the answer is degraded* below for the honest-signal contract that replaced it.

`IChatToolContributor.DocumentTypeCode` is **informational** — it is *not* a filter. Every contributor's tools are exposed on every turn regardless of conversation anchor. The field is used only as a tie-breaker hint for `MaxToolsPerTurn` trimming and to help reviewers understand intent. Do not rely on it for authorization (do that inside each tool body — see *Adding a tool contributor* below).

The hard cap on tool-call rounds within a single turn is configured at host wiring time via `PaperbaseAI:MaxToolIterations` (default `10`); see [ai-provider.md → Provider wiring](ai-provider.md#provider-wiring-paperbaseai). For prompt language behavior, see [ai-provider.md → Cross-cutting LLM behavior](ai-provider.md#cross-cutting-llm-behavior-paperbaseaibehavior). For retrieval `topK` / `minScore` defaults, see [knowledge-index.md](knowledge-index.md). For BM25-augmented hybrid retrieval, see [hybrid-search.md](hybrid-search.md).

## Tools

The agent's toolset is the union of the built-in tools below plus every `IChatToolContributor`-registered tool. The model picks when, in what order, and with what arguments to invoke each — there is no scripted workflow.

### Built-in tools

| Tool | What it does | Notes |
|---|---|---|
| `search_paperbase_documents` | Semantic vector search over the user's documents. The model supplies a natural-language `query`; optional `documentIds[]`, `documentTypeCode`, `topK`, `minScore` parameters let it drill in after a structured-tool round. | Tenant-scoped at the binding layer (`_tenantId` is captured in the closure, not derived from `DataFilter`). Defaults from `PaperbaseAIBehavior:ChatTopK` / `:ChatMinScore`. Citations from all calls in one turn are appended + deduplicated up to `MaxCapturedCitations`. |
| `get_document_relations` | Bidirectional lookup over the `DocumentRelation` aggregate — returns documents linked to a given `documentId` (manual + AI-discovered edges). Ordered by source (`Manual` first) then by `Confidence` desc, capped at 20 per call. | Requires `Paperbase.Documents.Default` (re-asserted inside the tool body). Powers cross-document reasoning chains like contract → matching invoices. See [relation-discovery.md](relation-discovery.md) for how the underlying graph is populated. |
| `get_document_outline` | Returns the Markdown heading tree (level + title + line number) of a single document by `documentId`. Body text is omitted — this is for structural navigation, not retrieval. Capped at 50 headers per call with a `truncated` flag. | Vector search struggles with questions like "how many sections" or "what's in chapter 3". This tool gives the model an O(1) view of structure so it can decide whether to drill in further with `get_document_excerpt` or fall back to vector search. Requires `Paperbase.Documents.Default`. Cross-tenant hits collapse to `not_found` (no existence leak). |
| `get_document_excerpt` | Exact-substring grep over a single document's Markdown body (case-insensitive), returning matching passages with 2 lines of context before/after each hit. Overlapping windows merge so consecutive hits don't return duplicated lines. Capped at 5 matches per call. | Vector embeddings underweight precise tokens — contract numbers, invoice IDs, specific dates, proper nouns. This tool is the literal-match complement to `search_paperbase_documents`. Requires `Paperbase.Documents.Default`. Same tenant isolation as outline. |

### Contributor tools

Every business module that wants to expose structured queries to the agent ships an `IChatToolContributor` (registered as `ITransientDependency`). Reference: `modules/contracts/src/Dignite.Paperbase.Contracts.Application/Chat/ContractChatToolContributor.cs` contributes:

- `search_contracts` — list contracts by counterparty / number / status
- `get_contract_detail` — fetch a single contract's structured fields
- `get_contract_aggregate` — sum amounts across a filtered contract set

See [Adding a tool contributor](#adding-a-tool-contributor-business-modules) for the contract every new contributor must follow.

### The fail-closed contract

Every tool — built-in or contributor — must satisfy the same three rules inside its method body, because the LLM (not the HTTP authorization filter) decides when to call it. HTTP-level `[Authorize]` on the AppService does **not** cover these calls.

1. **Explicit permission check** via `IAuthorizationService.CheckAsync(...)`. The Chat permission alone is insufficient — each tool re-asserts the feature permission relevant to its data.
2. **Explicit tenant predicate** in the query (`Where(x => x.TenantId == _tenantId)`). Do not rely on ABP's ambient `DataFilter` — any code path that disables it would silently leak across tenants.
3. **Hard `Take(N)` row cap** to defend against prompt-injection-driven recall bombs.

Reverse examples and the rationale: [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md), reverse examples C and D.

### Tool-call progress description

The streaming endpoint emits a `ToolCallStarted` delta when the model fires a tool. The user-facing label on that delta comes from the optional `progressDescriber` parameter on `IChatToolFactory.Create(..., progressDescriber)`:

```csharp
yield return toolFactory.Create(
    ctx,
    binding.SearchAsync,
    name: "search_contracts",
    description: "Search contracts by counterparty…",
    progressDescriber: args =>
    {
        if (args.TryGetValue("partyName", out _)) return "正在按甲方筛选合同…";
        if (args.TryGetValue("contractNumber", out _)) return "正在按合同编号查找合同…";
        return "正在检索合同…";
    });
```

The describer is **structural only** — it inspects which arguments the model supplied but **must never echo their values**. Echoing raw arguments would leak data before any per-tool permission check has fired (see [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md) reverse example C #4). Tools that omit `progressDescriber` get a generic fallback (`"正在执行 {toolName}…"`).

## Citation-to-source navigation

`ChatCitationDto` is the UI-facing citation contract. Citation navigation is Markdown-only: every source document type is handled through the extracted `Document.Markdown`, not through a PDF/image/original-file viewer.

| Field | Navigation meaning |
| --- | --- |
| `documentId` | The source document to open. A citation click must navigate to this document even when the active conversation is scoped by `documentTypeCode` and the cited document is not currently displayed. |
| `snippet` | Primary Markdown positioning key. Search the current document Markdown for this text and highlight the first matching range when possible. |
| `chunkIndex` | Optional knowledge-index chunk ordinal for display/debug context only. It is not a stable Markdown anchor after re-embedding and must not drive positioning by itself. |
| `sourceName` | Display label only. Do not parse it for routing or positioning. |

Fallback order:

1. If `documentId` is missing or the document cannot be loaded, keep the citation as non-navigable display text.
2. Open `documentId` in the chat source pane and render the persisted Markdown.
3. Try to locate `snippet` in the current Markdown and highlight the first matching range.
4. If `snippet` cannot be found, show the Markdown without a highlight and keep `chunkIndex` visible as citation context.

This deliberately does not introduce a separate `DocumentSourceLocation` DTO, PDF page navigation, persisted chunk IDs, or stored character offsets. Add exact offsets only after snippet matching proves insufficient in real use.

**Snippet match is whole-document `indexOf`.** The first occurrence of the snippet in the persisted Markdown is highlighted. Re-extracting the document with a different OCR run can shift the persisted Markdown enough that the snippet no longer matches; the UI surfaces this as a visible warning without breaking the chat.

### Developer notes

The source pane is intentionally AI-first. It must render the same Markdown artifact that retrieval, embedding, reranking, and citation snippets are based on. Do not add a parallel PDF/image/original-file source pane for citation navigation.

The original file is still available through the document detail experience, but that is an auxiliary inspection action. A chat citation click must stay on the Markdown source path: load `documentId`, render `Document.Markdown`, then try to highlight `snippet`.

`pageNumber` is not part of the navigation contract. The DTO may still carry it for backward compatibility or future metadata, but new UI and server code must not branch to a PDF viewer, append `#page=N`, or introduce `preferredView: 'pdf' | 'markdown'` based on it.

`chunkIndex` is not a durable anchor. Re-extraction, re-chunking, embedding option changes, or model changes can shift chunk ordinals. Use it in labels, diagnostics, and logs only; never make it the sole positioning key.

Do not add `preferredView` to server DTOs. There is only one citation source view today: Markdown. If exact positioning becomes necessary later, prefer adding Markdown offsets or persisted source ranges after measuring snippet-match failures in production data.

## Grounding source and degraded answers

Every turn carries a `groundingSource` enum on `ChatTurnResultDto` describing what kind of evidence the answer rests on:

| `groundingSource` | Meaning | `isDegraded` |
|---|---|---|
| `None` (0) | The model produced an answer **without invoking any tool** — no search, no contributor tool. Falls back to conversation history and the model's parametric knowledge. | `true` |
| `Vector` (1) | The model invoked `search_paperbase_documents` (and/or other vector-backed retrieval) at least once. Retrieved chunks are reflected in `citations`. | `false` |
| `Structured` (2) | The model invoked only structured/contributor tools (`search_contracts`, `get_contract_aggregate`, `get_document_relations`, …). The answer is grounded in business data, often without text citations. | `false` |
| `Mixed` (3) | The model invoked both vector search and structured tools in the same turn. | `false` |

`isDegraded` is therefore equivalent to `groundingSource == None`. The classification is performed by inspecting the turn's tool-call audit trail in `ChatTelemetryRecorder` — there is no separate flag the model can lie about.

| Cause | What happened | What to do |
|---|---|---|
| Knowledge index unavailable | `IDocumentKnowledgeIndex.SearchAsync` threw — Qdrant down, network fault, etc. The model may still call other tools or fall back to history. | Treat as a transient infrastructure incident. If the model still called structured tools, the turn is `Structured`, not `None`. |
| Model declined to invoke any tool | The model judged the question answerable without retrieval (greetings, follow-up clarifications). | Accept it: an empty `citations` array with `groundingSource = None` and `isDegraded = true` is the honest signal. If a class of questions is consistently answered without tool calls where you want them grounded, tighten the QA system prompt in `DefaultPromptProvider` rather than forcing pre-injection. |

`isDegraded` and `groundingSource` are both surfaced on the API response so the UI can show a "no sources used" banner or a "answered from contract data" badge.

## Auto-generated conversation titles

When the **first** message of a conversation arrives — defined as `ChatConversation.Messages.Count == 0` and the title still equal to the `Chat:UntitledConversation` localization placeholder — `ChatAppService.TryGenerateAndApplyTitleAsync` fires a small extra LLM call right after the main turn finishes. Its job is to produce a 2–3 word conversation title from `(user message, assistant answer)` so the UI sidebar shows something more informative than "Untitled conversation". The result is written via `conversation.Rename(title)` in the same unit of work.

This call is intentionally minimal:

| Property | Value |
|---|---|
| Prompt | The user's message and the assistant's reply, wrapped in `PromptBoundary` envelopes |
| Instructions | `IPromptProvider.GetConversationTitlePrompt(DefaultLanguage)` — explicitly disallows tool use, asks for a short title only |
| Token cost | ~200 input / ~5 output per turn (verified against SiliconFlow billing) |
| Failure mode | Try/catch around the whole call — if the LLM errors out, the conversation just keeps its "Untitled" title; no user-visible failure |

### Why it shows up as a second `orchestrate_tools` span on the trace

The title generator calls the **same** `_chatClient` registered in `PaperbaseHostModule.ConfigureAI`, which is wrapped with `.UseFunctionInvocation()`. The `FunctionInvokingChatClient` decorator emits an `orchestrate_tools` Activity span around every call regardless of whether tools actually get invoked. So a trace of "first turn of a new conversation" looks like this:

```
POST .../messages/stream
├── orchestrate_tools          ← the real chat turn (search/answer/etc.)
│   ├── chat <model>
│   ├── execute_tool <name>
│   └── chat <model>
└── orchestrate_tools          ← THIS ONE is the title generator
    └── chat <model>           ← input_tokens ≈ 200, output_tokens ≈ 5, no tool_calls
```

If you see exactly two `orchestrate_tools` siblings under the same root and the second one has tiny input/output and no `execute_tool` children, that's the title generator, not a bug. Subsequent turns in the same conversation produce only one `orchestrate_tools` span because `ShouldGenerateTitle` returns false after the first message lands.

### Disabling or replacing

Override `ShouldGenerateTitle` to return `false` (in a host-level subclass) to skip auto-titling entirely. Override `TryGenerateAndApplyTitleAsync` to plug in a different naming strategy (e.g. take the first 30 chars of the user message verbatim, no LLM call). The base method is `protected virtual` for that reason.

## Observability

Beyond the OpenTelemetry signals from `Microsoft.Extensions.AI` (see [ai-provider.md → OpenTelemetry signals](ai-provider.md#opentelemetry-signals)), each turn enriches the ABP audit row through `ChatTelemetryRecorder`. Two structured payloads are attached to `AbpAuditLogs.ExtraProperties`:

| Property key | Shape | Meaning |
| --- | --- | --- |
| `Chat.Turn` | `ChatTurnAuditEntry` (single object) | One per turn. Carries `ConversationId`, `Streaming`, `CitationCount`, `IsDegraded`, `ElapsedMs`, `Outcome`, plus the dimensions in the table below. |
| `Chat.ToolCalls` | `IReadOnlyList<ChatToolAuditEntry>` | One entry per tool invocation, in call order. Carries `ToolName`, `ArgumentsSummary` (sanitised — never raw user input), `ResultSizeBytes`, `ElapsedMs`, `Outcome`, `ExceptionType`. |

Notable fields on `Chat.Turn`:

| Field | Type | Meaning |
| --- | --- | --- |
| `GroundingSource` | enum | `None` / `Vector` / `Structured` / `Mixed` — derived by `ClassifyGrounding` from the per-tool entries, not a flag the model can set |
| `ToolCallDepth` | `int` | Total tool invocations in this turn (sum of `ToolCallSummary` values; includes failed invocations because they reflect actual model behaviour) |
| `ToolCallSummary` | `Dictionary<string,int>` | Per-tool invocation count, e.g. `{ "search_paperbase_documents": 2, "get_contract_detail": 1 }` |
| `CitationsTrimmed` | `bool` | `true` if `MaxCapturedCitations` was hit and additional vector-search hits were dropped |
| `AnchorResolutionFailed` | `bool` | `true` if the conversation has a `documentId` but the per-turn anchor lookup degraded (document deleted, tenant mismatch, or caller lost `Documents.Default`). The turn proceeds **without** the anchor hint — never throws 404. |

These dimensions are what drives the upgrade decision in the future: if production telemetry shows `ToolCallDepth > 8` for ≥ 20% of turns, that is the trigger to evaluate planner sub-agents (Magentic Orchestration). Until then, the single `ChatClientAgent` + flat tool list is intentionally kept simple — see [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md) reverse example D for the rationale against premature `AsAIFunction()` sub-agents.

## Adding a tool contributor (business modules)

To let the chat answer business-domain questions ("show contracts with Acme Corp expiring this quarter"), a business module implements `IChatToolContributor`. Three rules apply, each enforced at PR review:

1. **`ContributeTools` returns `AIFunction`s with static descriptions** — never interpolate user-controlled text into the description (prompt-injection vector).
2. **Each tool method is fail-closed**: explicit `IAuthorizationService.CheckAsync(...)` for the feature permission + explicit `Where(x => x.TenantId == _tenantId)` (do not rely on ABP's ambient `DataFilter`) + a hard `Take(N)` row cap.
3. **No raw SQL.** Compose queries via `IRepository<T>.GetQueryableAsync()` so all framework filters (soft-delete, tenant, audit) stay in effect.

Reference implementation: `modules/contracts/src/Dignite.Paperbase.Contracts.Application/Chat/ContractChatToolContributor.cs`. Counter-examples and the rationale: [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md).

## See also

- [HTTP client guide](chat-client.md) — request/response shapes, idempotency, 409 retry pattern
- [Knowledge index](knowledge-index.md) — what backs retrieval
- [Hybrid search](hybrid-search.md) — BM25 + dense recall fusion
- [Embedding pipeline](embedding.md) — where chunks come from
- [Relation discovery](relation-discovery.md) — populates the `DocumentRelation` graph the chat agent reaches via `get_document_relations`
