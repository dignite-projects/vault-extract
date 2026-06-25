import { mapEnumToOptions } from '@abp/ng.core';

export enum DocumentReviewDisposition {
  NotReviewed = 0,
  Confirmed = 20,
  Rejected = 30,
}

export const documentReviewDispositionOptions = mapEnumToOptions(DocumentReviewDisposition);
