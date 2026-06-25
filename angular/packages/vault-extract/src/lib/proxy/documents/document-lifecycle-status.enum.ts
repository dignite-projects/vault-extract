import { mapEnumToOptions } from '@abp/ng.core';

export enum DocumentLifecycleStatus {
  Uploaded = 10,
  Processing = 20,
  Ready = 30,
  Failed = 99,
}

export const documentLifecycleStatusOptions = mapEnumToOptions(DocumentLifecycleStatus);
