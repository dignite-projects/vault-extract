import { mapEnumToOptions } from '@abp/ng.core';

export enum PackItemAction {
  Created = 0,
  Updated = 1,
  Skipped = 2,
}

export const packItemActionOptions = mapEnumToOptions(PackItemAction);
