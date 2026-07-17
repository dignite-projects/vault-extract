import { firstValueFrom, of, throwError } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { executeBulkOperations } from './bulk-operation';

describe('executeBulkOperations', () => {
  it('collects successful items', async () => {
    const result = await firstValueFrom(executeBulkOperations([1, 2, 3], item => of(item)));

    expect(result).toEqual({ succeeded: [1, 2, 3], failed: [] });
  });

  it('keeps processing and reports individual failures', async () => {
    const result = await firstValueFrom(
      executeBulkOperations([1, 2, 3], item =>
        item === 2 ? throwError(() => new Error('blocked')) : of(item),
      ),
    );

    expect(result).toEqual({ succeeded: [1, 3], failed: [2] });
  });
});
