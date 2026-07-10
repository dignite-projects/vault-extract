import { mapEnumToOptions } from '@abp/ng.core';

export enum PackImportMode {
  CreateOrUpdate = 0,
  CreateOnly = 1,
}

export const packImportModeOptions = mapEnumToOptions(PackImportMode);
