# Observability

Dignite Vault Extract emits OpenTelemetry traces and metrics from the MAF + `Microsoft.Extensions.AI` stack it consumes, and ships them through a single host-configured export pipeline. This page covers what's emitted, how to wire it up locally, and how to point it at a production backend.

## What's emitted

| Source | Type | Highlights |
|---|---|---|
| **`Microsoft.Agents.AI`** | Traces, Metrics | MAF agent-invocation spans + token-usage / tool-call metrics for the `ChatClientAgent` runs (today: document classification via `RunAsync<T>`). MAF also defines `CompactionTelemetry` (`compaction.*` spans) but those only fire when chat-history compaction is configured — Dignite Vault Extract's agent runs are single-shot and tool-free, so no `compaction.*` spans are emitted. |
| **`Microsoft.Extensions.AI`** | Traces, Metrics | `chat-client.GetResponseAsync` spans with GenAI semantic-convention tags (model id, prompt / completion tokens, finish reason). Emitted automatically by the `.UseOpenTelemetry()` decorators wired on every chat client in `ExtractHostModule.ConfigureAI`. |
| **`Dignite.Vault.Extract.*`** | Traces, Metrics (reserved) | Wildcard reservation only — **nothing emits under it today**. Dignite Vault Extract Core registers no custom `ActivitySource` / `Meter` (every LLM stage — classification / field extraction / title generation / slug suggestion — is observable through the `Microsoft.Extensions.AI` + `Microsoft.Agents.AI` instrumentation above). The wildcard is a forward hook for **Dignite Vault Extract Core's own future pipeline metrics** (e.g. classification-confidence histograms, OCR-duration counters) to land in the pipeline without host-side changes. It is **not** an integration point for downstream business modules — those are out of scope and run in their own host with their own OTel pipeline. |

If Dignite Vault Extract Core later adds a Meter or ActivitySource named `Dignite.Vault.Extract.<name>`, it lands in the pipeline automatically — the host registers wildcard `AddSource("Dignite.Vault.Extract.*")` / `AddMeter("Dignite.Vault.Extract.*")`.

## Host pipeline configuration

The pipeline is set up in `host/src/ExtractHostModule.cs → ConfigureOpenTelemetry`. It's **opt-in** so an unconfigured host doesn't spawn a background exporter or hit a non-existent OTLP endpoint.

Default in `host/src/appsettings.json`:

```json
"OpenTelemetry": {
  "Enabled": false,
  "ConsoleExporter": false,
  "Otlp": {
    "Endpoint": "http://localhost:4317",
    "Protocol": "Grpc"
  }
}
```

| Key | Default | Description |
|---|---|---|
| `OpenTelemetry:Enabled` | `false` | Master switch. When `false`, `AddOpenTelemetry()` is not called at all — zero runtime cost. |
| `OpenTelemetry:ConsoleExporter` | `false` | Adds an extra console exporter alongside OTLP. Useful for one-off "is anything being emitted at all" sanity checks in containers without a dashboard. |
| `OpenTelemetry:Otlp:Endpoint` | `http://localhost:4317` | The OTLP collector endpoint. Falsy / empty disables the OTLP exporter (Console-only mode still works if enabled). |
| `OpenTelemetry:Otlp:Protocol` | `Grpc` | `Grpc` or `HttpProtobuf`. Most OTLP collectors accept both; pick whichever your network policy allows. |

The same overrides work via environment variables — replace `:` with `__`:

```bash
OpenTelemetry__Enabled=true
OpenTelemetry__Otlp__Endpoint=http://otel-collector.internal:4317
```

## Local development with Aspire Dashboard

For local dev we ship a profile-gated `aspire-dashboard` service in `host/docker-compose.yml`. It receives OTLP over gRPC and renders traces + metrics + logs at `http://localhost:18888`.

**Why aspire-dashboard for dev**: single container, zero-config, runs on the developer's laptop. Use Jaeger / Datadog / Grafana Tempo / Azure Monitor in shared environments; OTLP is vendor-neutral so the same instrumentation hits any backend.

### Bring it up

```powershell
cd host

# Profile-gated so plain `docker compose up` doesn't pull a 300MB image
docker compose --profile observability up -d aspire-dashboard
```

### Tell the host to send to it

Pick one of three places to set `OpenTelemetry:Enabled = true`:

| Where | Scope | Notes |
|---|---|---|
| `host/src/Properties/launchSettings.json` → `environmentVariables` | Per-launch-profile, **persisted in git** | Recommended for the project default. Already populated for both `IIS Express` and `Dignite.Vault.Extract.Host` profiles. |
| `host/src/appsettings.Development.json` → `OpenTelemetry.Enabled = true` | Development environment, **persisted in git** | Equivalent effect to launchSettings; choose one or the other (both is harmless but redundant). |
| Shell env vars (`$env:OpenTelemetry__Enabled = "true"` in PowerShell) | Current shell session only | For ad-hoc inspection without changing any tracked file. |

The repo defaults to **launchSettings.json**: contributors who clone, `docker compose --profile observability up -d`, then F5 / `dotnet run` immediately see signals on the dashboard with no further config.

### Verify

```powershell
# Start the host
dotnet run --project host/src/Dignite.Vault.Extract.Host.csproj

# Upload a document via the API or operator UI, then open the dashboard
start http://localhost:18888
```

Expected sightings:

- **Traces** tab — an ASP.NET Core request span (or background-job activity) containing nested `chat-client.GetResponseAsync` spans for classification / field extraction / title generation / slug suggestion. Classification additionally has a MAF `invoke_agent` span. No `execute_tool` children — all LLM calls are tool-free.
- **Metrics** tab — MAF + `Microsoft.Extensions.AI` token-usage counters tick on each LLM invocation.
- **Structured Logs** tab — Serilog logs with `TraceId` correlations to the spans on the left.

### First-start delay

aspire-dashboard takes 30–60 seconds to become reachable after `Up` status. If `http://localhost:18888` refuses, wait and retry — or check `docker compose logs aspire-dashboard | tail` for `Now listening on:`.

### Note: `gen_ai.usage.*` token counts are trustworthy here (no streaming)

All Dignite Vault Extract LLM calls (classification / field extraction / title generation / slug suggestion) are **non-streaming**. The `gen_ai.usage.input_tokens` / `gen_ai.usage.output_tokens` values reflect the provider-reported totals for the turn and are safe to use for cost tracking.

This only becomes a caveat if a future code path introduces *streaming* (`GetStreamingResponseAsync`): `Microsoft.Extensions.AI`'s `OpenTelemetryChatClient` accumulates usage per streamed chunk, and some OpenAI-compatible gateways report **cumulative-so-far** usage on each chunk rather than per-chunk deltas — the SDK then sums them, inflating the total by the chunk count (observed ~40–70× on SiliconFlow + DeepSeek-V3). If streaming is ever added, verify token numbers against the provider's billing dashboard before trusting OTel for absolute cost.

### Gotcha: the `Experimental.*` source-name prefix

MAF and `Microsoft.Extensions.AI` currently publish their ActivitySources and Meters under names with an **`Experimental.`** prefix:

| Library | Actual source name | Why |
|---|---|---|
| `Microsoft.Agents.AI` | `Experimental.Microsoft.Agents.AI` | Follows the OpenTelemetry GenAI semantic-convention draft (https://opentelemetry.io/docs/specs/semconv/gen-ai/) — the spec is still pre-stable so Microsoft scopes the telemetry under "Experimental" until conventions freeze. |
| `Microsoft.Extensions.AI` | `Experimental.Microsoft.Extensions.AI` | Same reason. |

The prefix will be dropped once the spec stabilizes. `ExtractHostModule.ConfigureOpenTelemetry` registers both the prefixed and unprefixed names so the pipeline keeps working through that rename.

**Symptom of forgetting the prefix**: Aspire Dashboard shows the bare `HTTP POST api.siliconflow.cn:443` (or other provider) spans from `System.Net.Http` instrumentation, but no wrapping `chat-client.GetResponseAsync` parent and no `execute_tool {tool_name}` children. The OpenTelemetry SDK silently drops spans from unregistered sources — no exception, no warning. If you see only HTTP leaf spans for an LLM call, this is the first thing to check.

## Pointing at a different OTLP backend

OTLP is vendor-neutral. To switch from aspire-dashboard to anything else, change only the endpoint:

```bash
# Jaeger (OTLP-native since 1.35)
OpenTelemetry__Otlp__Endpoint=http://jaeger:4317

# Grafana Tempo
OpenTelemetry__Otlp__Endpoint=http://tempo:4317

# Datadog (via OTel collector with the datadogexporter)
OpenTelemetry__Otlp__Endpoint=http://otel-collector:4317

# Azure Monitor: use OpenTelemetry.Exporter.AzureMonitor instead of OTLP
# (requires a code change in ExtractHostModule.ConfigureOpenTelemetry)
```

Production deployments should set the endpoint via env var or Kubernetes ConfigMap — never commit a production OTLP URL to `appsettings.json`.

## Tagging policy and cardinality

Any Dignite Vault Extract Core Meter introduced under `Dignite.Vault.Extract.*` should follow the same rule: **tags are low-cardinality enums or bounded sets**.

| Allowed as tag | Not allowed as tag |
|---|---|
| `document_type_code` (bounded by tenant-scoped `DocumentType` rows) | `tenant_id` (multi-tenant cardinality blowup) |
| `success` (`true` / `false`) | `user_id` |
| `pipeline_code` (one of the static `ExtractPipelines.*`) | `document_id` |
| `stage` (one of the static pipeline stage names) | Free-text from the model or user |

Per-tenant / per-user drill-down belongs in traces and structured logs — those are sampled, while metrics are aggregated by tag and would explode storage and dashboard latency.

When adding a new tag to an existing metric, audit the cardinality first. A tag that can grow unboundedly is a regression even if it "just works" for the first month.

## Tests

A test must not register the production OTel pipeline. The `ExtractHostModule.ConfigureOpenTelemetry` short-circuits when `Enabled = false` (the default), so test hosts that don't set `OpenTelemetry:Enabled = true` skip the export entirely. Tests that need to *capture* metric emissions instead use `System.Diagnostics.Metrics.MeterListener` directly — subscribe to the specific Meter name in test setup, drain measurements in assertions.

## Adding a Meter in Dignite Vault Extract Core

The `Dignite.Vault.Extract.*` wildcard exists so Dignite Vault Extract Core can add its own pipeline metrics later (e.g. classification-confidence histogram, OCR-duration counter) without touching the host. Pattern:

```csharp
// In Dignite Vault Extract Core (Domain or Application layer)
public class PipelineTelemetryRecorder : ISingletonDependency
{
    public const string MeterName = "Dignite.Vault.Extract.Pipeline";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> SomeCounter = Meter.CreateCounter<long>(
        "extract.pipeline.something.total",
        description: "...");

    public virtual void RecordSomething(string tagValue)
    {
        SomeCounter.Add(1, new KeyValuePair<string, object?>("dimension", tagValue));
    }
}
```

No host-side change required: the `AddMeter("Dignite.Vault.Extract.*")` / `AddSource("Dignite.Vault.Extract.*")` wildcards in `ConfigureOpenTelemetry` pick up any Core-defined Meter or ActivitySource under that prefix on the next host rebuild.

Downstream business modules are out of scope (they run in their own host with their own OTel pipeline), so this wildcard is **not** an integration seam for them.
