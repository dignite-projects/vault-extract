import { mapEnumToOptions } from '@abp/ng.core';

export enum ReclassificationScope {
  OnlyCurrentType = 0,
  AllDocuments = 10,
  PendingReviewQueue = 20,
}

export const reclassificationScopeOptions = mapEnumToOptions(ReclassificationScope);
