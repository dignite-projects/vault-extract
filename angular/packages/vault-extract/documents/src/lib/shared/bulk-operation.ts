import { Observable, catchError, from, map, mergeMap, of, toArray } from 'rxjs';

export interface BulkOperationResult<T> {
  succeeded: T[];
  failed: T[];
}

interface BulkOperationOutcome<T> {
  item: T;
  succeeded: boolean;
}

/**
 * Runs independent item operations with bounded concurrency and turns per-item errors into an
 * aggregate result. Callers can therefore report partial completion without one failed request
 * cancelling the remaining work.
 */
export function executeBulkOperations<T>(
  items: readonly T[],
  operation: (item: T) => Observable<unknown>,
  concurrency = 4,
): Observable<BulkOperationResult<T>> {
  return from(items).pipe(
    mergeMap(
      item =>
        operation(item).pipe(
          map((): BulkOperationOutcome<T> => ({ item, succeeded: true })),
          catchError(() => of<BulkOperationOutcome<T>>({ item, succeeded: false })),
        ),
      concurrency,
    ),
    toArray(),
    map(outcomes => ({
      succeeded: outcomes.filter(outcome => outcome.succeeded).map(outcome => outcome.item),
      failed: outcomes.filter(outcome => !outcome.succeeded).map(outcome => outcome.item),
    })),
  );
}
