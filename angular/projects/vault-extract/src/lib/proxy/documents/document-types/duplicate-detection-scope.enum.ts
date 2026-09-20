import { mapEnumToOptions } from '@abp/ng.core';

export enum DuplicateDetectionScope {
  Layer = 0,
  Uploader = 1,
}

export const duplicateDetectionScopeOptions = mapEnumToOptions(DuplicateDetectionScope);
