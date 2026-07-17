# AI Provider

Dignite Vault Extract delegates all chat-completion calls to `Microsoft.Extensions.AI`. AI configuration is split into two disjoint sections:

| Section | Owns | Consumed by |
| --- | --- | --- |
| `Extract` | Provider wiring (endpoint, credentials, model ids) | Host only — `ExtractHostModule.ConfigureAI` reads it once at startup to register two keyed `IChatClient` instances |
| `ExtractBehavior` | Workflow behavior knobs (prompt language, truncation) | Application layer via `IOptions<ExtractBehaviorOptions>` — `ExtractApplicationModule.ConfigureServices` binds the section to the type |

The split keeps credentials (`ApiKey`) out of any `IOptions<>` flowing into business code and lets operators tune behavior independently of provider switches. Every downstream feature — [classification](../pipeline/classification.md), Host field extraction, tenant field extraction (mechanism B), document title generation — shares the same provider registration regardless of behavior tuning.

> **Dignite Vault Extract is a channel layer.** It does not host chat / RAG / agentic tool-calling paths (those were removed in #166 — see CLAUDE.md "OUT of scope"). The LLM call sites in this repo are backend pipeline workflows plus the admin-facing slug suggestion helper. Downstream RAG / Chat consumers register their own `IChatClient` against their own provider on their side.

## Required before first run

An LLM provider is **mandatory**. `DocumentClassificationWorkflow` and `FieldExtractionWorkflow` have no non-LLM fallback, so a host with no provider cannot classify documents or extract fields. To make this unmissable, `ExtractHostModule.ConfigureAI` **fails fast at startup**: if `Vault:Extract:Endpoint`, `Vault:Extract:ApiKey`, or `Vault:Extract:ChatModelId` is missing — or `ApiKey` is still the committed `"YOUR_API_KEY"` placeholder — the host throws before serving any request, with a message pointing here.

Supply credentials in `host/src/appsettings.Development.json` (git-ignored), [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), or environment variables. **Do not** edit the committed `appsettings.json` — its placeholder is exactly what the startup guard checks against, and a real key there would leak into source control. The cheapest first-run path is a local [Ollama](#trying-alternative-providers) endpoint; otherwise any OpenAI-compatible provider works (see [Picking a chat model](#picking-a-chat-model)).

## Provider wiring (`Extract`)

```json
"Vault": {
  "Extract": {
    "Endpoint": "https://api.openai.com/v1",
    "ApiKey": "YOUR_API_KEY",
    "ChatModelId": "gpt-4o-mini"
  }
}
```

| Key | Required | Description |
| --- | --- | --- |
| `Endpoint` | yes | API base URL. Any OpenAI-compatible `/v1` endpoint works (Azure OpenAI, Ollama, OpenRouter, vLLM, etc.) |
| `ApiKey` | yes | API key for the provider |
| `ChatModelId` | yes | Fallback model for both keyed chat clients when `TitleGeneratorModelId` / `StructuredModelId` are not overridden |
| `TitleGeneratorModelId` | optional | Model for the title-generation keyed client. Defaults to `ChatModelId`. Point at a smaller / faster model if cost matters |
| `StructuredModelId` | optional | Model for the structured-output keyed client. Defaults to `ChatModelId`. Point at a stronger model if classification / field-extraction quality matters |

## Picking a chat model

The only capability Dignite Vault Extract's backend LLM calls need is **structured JSON output**:

| Capability | Where it's used | Failure mode if weak |
|---|---|---|
| Structured JSON output (`response_format: json_schema`) | `DocumentClassificationWorkflow` (via MAF `RunAsync<T>` + schema-bound `T`), `FieldExtractionWorkflow` (via `ChatResponseFormat.ForJsonSchema` + per-field prompt, covering both Host fields and tenant fields under mechanism B), `SlugSuggestionAppService` (schema-bound `{ slug }`) | Returns malformed JSON or violates schema → classification falls back to `(unclassified)`, fields write as `null`, slug suggestion returns empty and the UI falls back |

That's it. Function calling, tool-call willingness, large-context RAG, multi-turn coherence — none of those matter here because Dignite Vault Extract has no Chat / RAG path. Even small open-source models (Qwen3-8B class) usually comply when the prompt explicitly demands a JSON object.

### Practical guidance

For **production**, prefer a model that's strict about schema compliance. Models that "almost get JSON right" cost you silent nulls — malformed output is coerced to `null` fields and the document routes to manual review. The choice is between cost and quality, not between "supports the protocol" vs "doesn't":

| Tier | Examples (OpenAI-compatible providers) | Trade-off |
|---|---|---|
| **Recommended** | `gpt-4o-mini` / `gpt-4o`, `claude-sonnet-4-6` (via shim), `Qwen/Qwen3-32B`, `deepseek-ai/DeepSeek-V3` | Schema-bound output reliable; field extraction returns useful values most of the time |
| **Budget** (development only) | `Qwen/Qwen3-8B`, small Llama variants | JSON shape usually correct; field values less reliable (numeric coercion failures, missed fields). Useful for smoke-testing the wiring without paying production tokens |

`TitleGeneratorModelId` can run on a cheaper / smaller model than `StructuredModelId` because title generation is plain text completion (no schema, no field-value reliability requirements).

## Keyed clients

`ExtractHostModule.ConfigureAI` registers exactly two keyed `IChatClient` instances. **There is no default (non-keyed) `IChatClient` registration** — Dignite Vault Extract has no main "chat" path, so every consumer pulls the keyed client appropriate to its workload:

| DI key | Consumed by | Why this exists separately |
|---|---|---|
| `ExtractConsts.TitleGeneratorChatClientKey` | `DocumentParseBackgroundJob.TryGenerateTitleAsync` (auto-generates a short document title from extracted Markdown) | Single-shot text completion, no schema. Different model id lets hosts run a cheaper / faster model here without affecting classification / extraction quality |
| `ExtractConsts.StructuredChatClientKey` | `DocumentClassificationWorkflow`, `FieldExtractionWorkflow` (unified Host + tenant field entry under mechanism B, called by `FieldExtractionEventHandler`), `SlugSuggestionAppService` | All schema-bound `RunAsync<T>` / `ChatResponseFormat.ForJsonSchema` calls share this client. Splitting structured from title lets production teams tune quality vs cost per workload |

Both clients are registered with `UseOpenTelemetry()` + `UseLogging()`. Neither has `UseFunctionInvocation` (no tool calling anywhere in Dignite Vault Extract) or `UseDistributedCache` (every prompt is document-content-derived and therefore unique per call — cache lookups would always miss).

> **Optional third client — vision OCR.** Enabling the [vision-LLM OCR provider](../text-extraction/ocr-vision-llm.md) (#259) adds a third keyed client for a multimodal (vision) model. Its key (`VisionLlmOcrConsts.VisionChatClientKey`) lives in the `Dignite.Vault.Extract.Ocr.VisionLlm` project, **not** `ExtractConsts` — an OCR provider sits below the Application layer and must not depend on it. Register it in your `ConfigureAI` override only when you enable that provider; the vision model id **cannot** fall back to `ChatModelId` (the main chat model may not be vision-capable), so it requires its own `Vault:Extract:VisionOcrModelId`.

> **Provider-switch gotcha**: When switching `Endpoint` to a non-OpenAI provider (SiliconFlow, Ollama via `/v1` shim, OpenRouter, etc.), override **all three** model id keys together in your environment-specific config — `ChatModelId` alone is not enough if the provider doesn't recognize the default `gpt-4o-mini` placeholder that may be inherited from base `appsettings.json`. The simplest fix: copy all three overrides into your `appsettings.Development.json` / `appsettings.Production.json` / env vars whenever you change `Endpoint`.

To split further (e.g. a different model per workflow), add more per-purpose `AddKeyedChatClient` registrations in your own `ConfigureAI` override. The current consolidation puts classification + field extraction (unified Host + tenant path) + slug suggestion on the same key because their call shape is identical (schema-bound JSON); split them only when production telemetry shows a real per-task cost or quality reason.

## Where it is used

| Caller | Keyed client |
|---|---|
| `Documents/Pipelines/Classification/DocumentClassificationWorkflow` | `StructuredChatClientKey` |
| `Documents/Pipelines/FieldExtraction/FieldExtractionWorkflow` (unified Host + tenant fields under mechanism B) | `StructuredChatClientKey` |
| `Documents/Pipelines/Parse/DocumentParseBackgroundJob.TryGenerateTitleAsync` | `TitleGeneratorChatClientKey` |
| `Slugging/SlugSuggestionAppService` | `StructuredChatClientKey` |

A single `Extract` block serves all of them. Picking different models per workflow is a host-level customization that replaces the registrations in `ExtractHostModule.ConfigureAI`.

## Trying alternative providers

- **Azure OpenAI**: set `Endpoint` to `https://<resource>.openai.azure.com/openai/deployments/<deployment>/` and use the deployment name as `ChatModelId`. (API-key auth only — for Microsoft Entra ID see [Going off-protocol](#going-off-protocol-non-openai-providers).)
- **Ollama (local)**: run `ollama serve`, set `Endpoint` to `http://localhost:11434/v1`, set `ApiKey` to any non-empty token (the `/v1` shim ignores it, but the OpenAI client rejects an empty key), and pick a locally pulled model id.
- **Any OpenAI-compatible gateway** (OpenRouter, vLLM, LM Studio, etc.) works the same way — only the keys in `Extract` need to change.

## Going off-protocol (non-OpenAI providers)

The `Extract` keys above only describe **OpenAI-protocol** providers because `ExtractHostModule.ConfigureAI` builds them against `OpenAIClient`. Targeting a provider that speaks a different wire protocol — native Anthropic Claude, native Google Gemini, AWS Bedrock, Microsoft Entra ID-authenticated Azure OpenAI, native Ollama without the `/v1` shim — means **replacing `ConfigureAI` in your host project**. Application code (the four workflows above) consumes the registered keyed `IChatClient` and is unchanged.

The `Extract` section becomes irrelevant in that case — drop it from `appsettings.json` and read your provider's keys from a configuration shape that matches its credential model (token, region, deployment id, etc.). `ExtractBehavior` still applies and is provider-agnostic.

### Invariants the new wiring must preserve

| Required | Why |
| --- | --- |
| Two `services.AddKeyedChatClient(key, factory)` registrations, keyed `ExtractConsts.TitleGeneratorChatClientKey` and `ExtractConsts.StructuredChatClientKey` | The workflow / background-job / AppService call sites inject by `[FromKeyedServices(...)]`. Missing either key crashes service resolution at first use |
| `.UseOpenTelemetry()` on both keyed clients | `gen_ai.*` semantic-convention spans + token counters depend on this decorator. Without it the OTel pipeline still emits MAF spans (from `Microsoft.Agents.AI`) but no per-LLM-call duration / token metrics |
| `.UseLogging()` on both keyed clients (recommended) | Per-call request / response logging at `Debug` is useful for diagnosing prompt issues. Drop only if your logs are token-budget constrained |

Things that are **NOT** required: `UseFunctionInvocation` (Dignite Vault Extract calls no tools), `UseDistributedCache` (every prompt is unique per call), `IEmbeddingGenerator` registration (no embedding pipeline in Dignite Vault Extract Core — vectorization is downstream RAG's responsibility).

### Sketch: Azure OpenAI with Microsoft Entra ID (no API key)

Replaces API-key auth with `DefaultAzureCredential`, recommended for Azure deployments with managed identity / workload identity. Requires `Azure.AI.OpenAI` and `Azure.Identity` package references in the host project:

```csharp
using Azure.AI.OpenAI;
using Azure.Identity;

private void ConfigureAI(ServiceConfigurationContext context, IConfiguration configuration)
{
    var azureClient = new AzureOpenAIClient(
        new Uri(configuration["AzureOpenAI:Endpoint"]!),
        new DefaultAzureCredential());

    var titleDeployment = configuration["AzureOpenAI:TitleDeployment"]
        ?? configuration["AzureOpenAI:ChatDeployment"]!;
    context.Services.AddKeyedChatClient(
        ExtractConsts.TitleGeneratorChatClientKey,
        _ => azureClient.GetChatClient(titleDeployment).AsIChatClient())
        .UseOpenTelemetry()
        .UseLogging();

    var structuredDeployment = configuration["AzureOpenAI:StructuredDeployment"]
        ?? configuration["AzureOpenAI:ChatDeployment"]!;
    context.Services.AddKeyedChatClient(
        ExtractConsts.StructuredChatClientKey,
        _ => azureClient.GetChatClient(structuredDeployment).AsIChatClient())
        .UseOpenTelemetry()
        .UseLogging();
}
```

### Sketch: native Ollama (without the OpenAI `/v1` shim)

Uses `OllamaSharp` directly, which exposes Ollama-only features (native tool format, model warm-up control) the OpenAI `/v1` shim doesn't expose. Requires the `OllamaSharp` package in the host project:

```csharp
using OllamaSharp;

var endpoint = new Uri(configuration["Ollama:Endpoint"]!);

context.Services.AddKeyedChatClient(
    ExtractConsts.TitleGeneratorChatClientKey,
    _ => new OllamaApiClient(endpoint, configuration["Ollama:TitleModelId"]!))
    .UseOpenTelemetry()
    .UseLogging();

context.Services.AddKeyedChatClient(
    ExtractConsts.StructuredChatClientKey,
    _ => new OllamaApiClient(endpoint, configuration["Ollama:StructuredModelId"]!))
    .UseOpenTelemetry()
    .UseLogging();
```

For the full set of `IChatClient`-compatible providers (Anthropic, Microsoft Foundry, GitHub Copilot, A2A, custom, …) and the exact factory calls each one ships, see the [Microsoft.Extensions.AI provider list](https://learn.microsoft.com/agent-framework/agents/providers/).

## Cross-cutting LLM behavior (`ExtractBehavior`)

These knobs describe *how Dignite Vault Extract calls the model* (language hint, text truncation). They are bound to `ExtractBehaviorOptions` and reach every pipeline through `IOptions<>`.

```json
"Vault": {
  "ExtractBehavior": {
    "DefaultLanguage": "ja",
    "MaxTextLengthPerExtraction": 8000,
    "MaxFieldExtractionMarkdownLength": 200000,
    "MaxFieldSchemaPromptLength": 32000
  }
}
```

| Key | Default | Description |
| --- | --- | --- |
| `DefaultLanguage` | `"ja"` | Language hint appended to every system prompt. Match this to your primary user base — Dignite Vault Extract prompts are written language-agnostic and switch via this hint |
| `MaxTextLengthPerExtraction` | `8000` | Per-call character cap on Markdown fed to **classification** and **cabinet suggestion** (both only need the document's opening for a verdict). CJK-safe (one character ≈ one CJK glyph). Raise for long contracts / policies if your model's context window allows |
| `MaxFieldExtractionMarkdownLength` | `200000` | Maximum complete document body accepted by field extraction. Field extraction cannot safely truncate because a field may appear anywhere; an oversized document is declined with a blocking review reason instead of calling the model |
| `MaxFieldSchemaPromptLength` | `32000` | Maximum total prompt characters across all active field definitions on one document type. Individual prompts remain uncapped; schema create/update/restore/import rejects only when the per-type total exceeds this host-funded LLM budget |

Per-pipeline tuning lives in `ExtractBehavior` — see [classification.md](../pipeline/classification.md) for the keys the classification workflow reads.

## OpenTelemetry signals

`ExtractHostModule.ConfigureAI` wires `Microsoft.Extensions.AI`'s `UseOpenTelemetry()` decorator onto both keyed chat clients. To collect these signals, register an OTel exporter in your host and add the source / meter names:

```csharp
context.Services.AddOpenTelemetry()
    .WithTracing(b => b
        .AddSource("Experimental.Microsoft.Extensions.AI")
        .AddSource("Microsoft.Extensions.AI")
        .AddSource("Experimental.Microsoft.Agents.AI")
        .AddSource("Microsoft.Agents.AI")
        .AddSource("Dignite.Vault.Extract.*"))            // reserved for project-specific spans
    .WithMetrics(b => b
        .AddMeter("Experimental.Microsoft.Extensions.AI")
        .AddMeter("Microsoft.Extensions.AI")
        .AddMeter("Experimental.Microsoft.Agents.AI")
        .AddMeter("Microsoft.Agents.AI")
        .AddMeter("Dignite.Vault.Extract.*"));            // reserved for project-specific meters
```

This matches what `ExtractHostModule.ConfigureOpenTelemetry` already adds when `OpenTelemetry:Enabled = true` in your host's `appsettings.json`.

| Source / Meter | Emitted by | What it covers |
| --- | --- | --- |
| `Experimental.Microsoft.Extensions.AI` (Activity + Meter) | `OpenTelemetryChatClient` (the `UseOpenTelemetry` decorator on each keyed client) | OTel GenAI semantic conventions: `chat {model}` / `execute_tool {name}` spans, `gen_ai.client.operation.duration` (s), `gen_ai.client.token.usage` |
| `Experimental.Microsoft.Agents.AI` (Activity + Meter) | MAF agent runtime (`ChatClientAgent.RunAsync<T>` etc.) | Agent-level spans wrapping each `RunAsync` call — useful for tying classification workflow steps together |
| `Dignite.Vault.Extract.*` (reserved) | None in Dignite Vault Extract Core today | Reserved wildcard for downstream consumers' module-specific telemetry (e.g. a `Dignite.Vault.Extract.Contracts` meter emitted by a downstream Contracts consumer in its own repo) |

Dignite Vault Extract Core currently emits **no project-specific telemetry meters of its own**. The `Dignite.Vault.Extract.*` wildcard exists for future use and for downstream consumer modules to plug in their extraction telemetry without each one needing a host-side config change.
