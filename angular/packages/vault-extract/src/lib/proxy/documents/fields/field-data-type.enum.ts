import { mapEnumToOptions } from '@abp/ng.core';

export enum FieldDataType {
  Text = 0,
  Number = 1,
  Boolean = 2,
  Date = 3,
  DateTime = 4,
  LongText = 5,
}

export const fieldDataTypeOptions = mapEnumToOptions(FieldDataType);
