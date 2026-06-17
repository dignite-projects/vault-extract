import { describe, expect, it } from 'vitest';

import {
  DocumentReviewDisposition,
  documentReviewDispositionOptions,
} from './proxy/documents/document-review-disposition.enum';
import {
  DocumentReviewReasons,
  documentReviewReasonsOptions,
} from './proxy/documents/document-review-reasons.enum';
import { DocumentLifecycleStatus } from './proxy/documents/document-lifecycle-status.enum';
import { PipelineRunStatus } from './proxy/documents/pipelines/pipeline-run-status.enum';
import {
  ReclassificationScope,
  reclassificationScopeOptions,
} from './proxy/documents/reprocessing/reclassification-scope.enum';

// Contract smoke test for generator-produced enums (`nx g @abp/ng.schematics:proxy-add`).
// Lives OUTSIDE proxy/ so it survives regeneration (the generator overwrites proxy/ and
// emits no specs). Guards that the numeric values the frontend renders/branches on stay in
// sync with the backend Domain.Shared enums — a renumbered or dropped member fails here
// loudly instead of silently mis-rendering a badge or mis-gating an action.
describe('proxy enum contract (smoke)', () => {
  it('DocumentReviewDisposition matches backend values', () => {
    expect(DocumentReviewDisposition.NotReviewed).toBe(0);
    expect(DocumentReviewDisposition.Confirmed).toBe(20);
    expect(DocumentReviewDisposition.Rejected).toBe(30);
    expect(documentReviewDispositionOptions).toHaveLength(3);
  });

  it('DocumentReviewReasons matches backend values', () => {
    expect(DocumentReviewReasons.None).toBe(0);
    expect(DocumentReviewReasons.UnresolvedClassification).toBe(1);
    expect(DocumentReviewReasons.MissingRequiredFields).toBe(2);
    expect(DocumentReviewReasons.SegmentationIncomplete).toBe(4);
    expect(documentReviewReasonsOptions).toHaveLength(4);
  });

  it('DocumentLifecycleStatus matches backend values', () => {
    expect(DocumentLifecycleStatus.Uploaded).toBe(10);
    expect(DocumentLifecycleStatus.Processing).toBe(20);
    expect(DocumentLifecycleStatus.Ready).toBe(30);
    expect(DocumentLifecycleStatus.Failed).toBe(99);
  });

  it('PipelineRunStatus matches backend values', () => {
    expect(PipelineRunStatus.Pending).toBe(10);
    expect(PipelineRunStatus.Running).toBe(20);
    expect(PipelineRunStatus.Succeeded).toBe(30);
    expect(PipelineRunStatus.Failed).toBe(90);
    expect(PipelineRunStatus.Skipped).toBe(95);
  });

  it('ReclassificationScope matches backend values (#289)', () => {
    expect(ReclassificationScope.OnlyCurrentType).toBe(0);
    expect(ReclassificationScope.AllDocuments).toBe(10);
    expect(ReclassificationScope.PendingReviewQueue).toBe(20);
    expect(reclassificationScopeOptions).toHaveLength(3);
  });
});
