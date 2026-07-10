import { EntityProp, ExtensionsService } from '@abp/ng.components/extensible';
import type { ABP } from '@abp/ng.core';

export const EXTRACT_TABLES = {
  Cabinets: 'VaultExtract.Cabinets',
  Documents: 'VaultExtract.Documents',
  DocumentRecycleBin: 'VaultExtract.DocumentRecycleBin',
  DocumentTypes: 'VaultExtract.DocumentTypes',
  FieldDefinitions: 'VaultExtract.FieldDefinitions',
} as const;

export interface ClientPagedResult<T> {
  totalCount: number;
  items: T[];
}

export type SortValue = boolean | Date | number | string | null | undefined;
export type SortAccessors<T> = Record<string, (item: T) => SortValue>;

export function configureEntityTable<T>(
  extensions: ExtensionsService,
  identifier: string,
  props: EntityProp<T>[],
): void {
  const entityProps = extensions.entityProps.get(identifier);
  entityProps.clearContributors();
  entityProps.addContributor(propList => propList.addManyTail(props));
}

export function pageClientItems<T>(
  items: T[],
  query: Partial<ABP.PageQueryParams>,
  sortAccessors: SortAccessors<T> = {},
): ClientPagedResult<T> {
  const sorted = sortItems(items, query.sorting, sortAccessors);
  const skipCount = query.skipCount ?? 0;
  const maxResultCount = query.maxResultCount ?? sorted.length;

  return {
    totalCount: sorted.length,
    items: sorted.slice(skipCount, skipCount + maxResultCount),
  };
}

function sortItems<T>(
  items: T[],
  sorting: string | undefined,
  sortAccessors: SortAccessors<T>,
): T[] {
  if (!sorting) return [...items];

  const [sortKey, sortOrder] = sorting.split(' ');
  if (!sortKey) return [...items];

  const direction = sortOrder?.toLowerCase() === 'desc' ? -1 : 1;
  const getValue = sortAccessors[sortKey] ?? ((item: T) => (item as Record<string, SortValue>)[sortKey]);

  return [...items].sort((a, b) => compareValues(getValue(a), getValue(b)) * direction);
}

function compareValues(a: SortValue, b: SortValue): number {
  if (a == null && b == null) return 0;
  if (a == null) return 1;
  if (b == null) return -1;

  const left = a instanceof Date ? a.getTime() : a;
  const right = b instanceof Date ? b.getTime() : b;

  if (typeof left === 'string' && typeof right === 'string') {
    return left.localeCompare(right);
  }

  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}
