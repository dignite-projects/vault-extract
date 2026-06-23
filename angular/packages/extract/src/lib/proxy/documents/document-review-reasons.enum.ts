import { mapEnumToOptions } from '@abp/ng.core';

export enum DocumentReviewReasons {
  None = 0,
  UnresolvedClassification = 1,
  MissingRequiredFields = 2,
  SegmentationIncomplete = 4,
  DuplicateSuspected = 8,
}

export const documentReviewReasonsOptions = mapEnumToOptions(DocumentReviewReasons);
