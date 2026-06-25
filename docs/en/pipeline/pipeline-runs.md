# Pipeline Runs

`DocumentPipelineRun` records the execution history of document processing pipelines. Common state such as `PipelineCode`, `Status`, `AttemptNumber`, `StartedAt`, `CompletedAt`, and `StatusMessage` is stored as first-class properties.

Pipeline-specific outputs are stored in `DocumentPipelineRun.ExtraProperties`. Keys are defined in `PipelineRunExtraPropertyNames`; each pipeline owns the shape of its own payload.

## Classification Candidates

`PipelineRunExtraPropertyNames.ClassificationCandidates` uses the key:

```csharp
Candidates
```

This payload is written when classification finishes with low confidence. It exists so the Angular UI can show the top candidate document types to a reviewer.

The server does not currently consume this payload for business decisions after writing it. Treat it as a UI-facing payload, not a domain rule source.

### Two-channel exposure

The payload is exposed to clients through **two** channels — both backed by the same `ExtraProperties["Candidates"]` storage:

1. **`DocumentPipelineRunDto.Candidates`** — strong-typed property of type `IReadOnlyList<PipelineRunCandidate>?` (or `PipelineRunCandidate[] | null` in TypeScript). **Prefer this.** abp generate-proxy emits the matching TS interface so Angular gets IDE autocomplete.
2. **`DocumentPipelineRunDto.ExtraProperties["Candidates"]`** — the generic `ExtraProperties` bag still carries the same array. Kept for the generic per-pipeline payload contract; do not read by key when the strong-typed property is available.

### JSON Shape

The payload is a JSON array. Each item follows the `PipelineRunCandidate` schema (defined in `Dignite.Vault.Extract.Documents` under Domain.Shared):

```json
[
  { "typeCode": "contract.general", "confidenceScore": 0.64 },
  { "typeCode": "invoice.standard", "confidenceScore": 0.31 }
]
```

| Property | Type | Description |
|----------|------|-------------|
| `typeCode` | `string` | Candidate document type code |
| `confidenceScore` | `number` | Classification confidence, expected in the `0.0` to `1.0` range |

### Server-Side Notes

Writing happens inside `DocumentPipelineRunManager.CompleteClassificationWithLowConfidenceAsync`:

```csharp
run.SetProperty(
    PipelineRunExtraPropertyNames.ClassificationCandidates,
    candidates);   // IReadOnlyList<PipelineRunCandidate>
```

Reading on the server normally goes through `DocumentPipelineRunToDocumentPipelineRunDtoMapper`: it wraps Mapperly's generated `Map`, calls a private `ExtractCandidates` after the partial mapping completes, and assigns the deserialized list to `DocumentPipelineRunDto.Candidates`. The extractor handles both the raw `IReadOnlyList<PipelineRunCandidate>` (same UoW, before persistence) and the `System.Text.Json.JsonElement` (after EF Core round-trip — ABP restores non-primitive entries as `JsonElement`, so `GetProperty<List<PipelineRunCandidate>>()` will *not* work; the extractor parses the JSON explicitly). The DTO's `Candidates` property is `{ get; set; }` so HTTP / STJ round-trips through `HttpApi.Client` work without going back through `ExtraProperties`.

### Angular Notes

Use `run.candidates` directly. Treat `null` as "no low-confidence candidate list" — do not coalesce to an empty array if you need to distinguish "no review needed" from "reviewed and resolved without candidates".
