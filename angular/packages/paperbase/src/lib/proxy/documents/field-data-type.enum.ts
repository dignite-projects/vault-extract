import { mapEnumToOptions } from '@abp/ng.core';

// Mirrors C# Dignite.Paperbase.Documents.FieldDataType (Domain.Shared).
export enum FieldDataType {
  String = 0,
  Number = 1,
  Boolean = 2,
  Date = 3,
  DateTime = 4,
}

export const fieldDataTypeOptions = mapEnumToOptions(FieldDataType);
