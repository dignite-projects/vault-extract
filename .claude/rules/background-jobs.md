---
description: "ABP background job unit of work boundaries and long-running work rules"
paths:
  - "core/src/**/*BackgroundJob*.cs"
  - "core/src/**/*Job.cs"
  - "core/src/**/*JobArgs.cs"
---

# ABP Background Job Rules

## Unit Of Work Boundaries

Do not wrap an entire background job `ExecuteAsync` in one long `[UnitOfWork]` when the job performs slow or external work, including but not limited to:

- blob or file IO
- OCR
- LLM or AI provider calls
- HTTP calls or other network IO
- long CPU-bound processing

Use short unit-of-work phases instead:

1. Begin phase: load the required aggregate(s), create or mark execution state as running, save, and commit.
2. External phase: perform slow IO, AI, network, or CPU-bound work outside any ambient UoW.
3. Complete phase: reload the aggregate(s), apply success/failure/skipped state, save, and commit.

This avoids holding database connections, locks, or transactions while external work is running. Long ambient UoW scopes can block normal HTTP reads and cause SQL command timeouts even for simple primary-key queries.

## Aggregate Persistence

- Modify child entities through their aggregate root.
- Do not introduce repositories for child entities to work around persistence issues.
- If a job carries an execution/run identifier in its args, persist that same identifier before enqueueing or beginning the job, and use it when completing or failing the job.

### DocumentPipelineRun Exception (#216)

`DocumentPipelineRun` is itself an `AggregateRoot<Guid>`, so it is handled directly through `IDocumentPipelineRunRepository` and **no longer through the `Document` aggregate root**. The BeginRun / CompleteRun / FailRun phases in the three-phase UoW pattern used by background jobs (`DocumentParseBackgroundJob` / `DocumentClassificationBackgroundJob`) all follow this model:

- Load the Document with `DocumentRepository.GetAsync(id, includeDetails: false)` (**not** `GetWithPipelineRunsAsync`).
- Load the specific run with `RunRepository.FindAsync(runId)` (**not** `document.GetRun(runId)`).
- Change run state through `DocumentPipelineRunManager` (the manager explicitly persists through `_runRepo.UpdateAsync`).
- When the same UoW commits, flush the Document row UPDATE and the PipelineRun row UPDATE / INSERT together.

The CompleteRun / FailRun phases share the same prelude, "load Document + locate the run by runId (fallback to reconstruction when missing)", and both job types share identical failure finalization. These shared parts live in the base class `DocumentPipelineBackgroundJobBase<TArgs>` (`LoadDocumentAndRunAsync` / `FailRunAsync`, #216 follow-up #2); each concrete job only implements its distinct Begin / Complete body.

**This is the only exception for this entity**. Other pipeline-related child entities (for example a future ChunkBlock) still follow the standard "access through the aggregate root" model. See Issue #216 for the decision record.

## Tests

When changing a background job that performs slow or external work, keep or add tests that verify the external work runs without an ambient UoW. A direct assertion such as `_unitOfWorkManager.Current.ShouldBeNull()` at the external call boundary is preferred.
