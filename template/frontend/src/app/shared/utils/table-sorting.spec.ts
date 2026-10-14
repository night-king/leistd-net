import { TestBed } from '@angular/core/testing';
import { ColumnDef, SortingState } from '@tanstack/angular-table';

import { injectAppTable } from './table-features';
import { tableSortAria } from './table-sorting';

import type { AppTableFeatures } from './table-features';

interface TestRow {
  name: string;
  status: string;
  created: string;
}

describe('tableSortAria', () => {
  const columns: ColumnDef<AppTableFeatures, TestRow>[] = [
    { accessorKey: 'name', id: 'name', meta: { priority: 'primary' } },
    { accessorKey: 'status', id: 'status', enableSorting: false, meta: { priority: 'secondary' } },
    { accessorKey: 'created', id: 'created', meta: { priority: 'tertiary' } },
  ];
  function createTable(sorting: SortingState = []) {
    return TestBed.runInInjectionContext(() =>
      injectAppTable(() => ({
        data: [],
        columns,
        state: { sorting },
        manualSorting: true,
      })),
    );
  }

  it('omits the attribute for a sortable column that is not sorted', () => {
    expect(tableSortAria(createTable(), 'name')).toBeNull();
  });

  it('follows the sort direction of a sortable column', () => {
    expect(tableSortAria(createTable([{ id: 'name', desc: false }]), 'name')).toBe('ascending');
    expect(tableSortAria(createTable([{ id: 'name', desc: true }]), 'name')).toBe('descending');
  });

  it('omits the attribute for a column that cannot be sorted', () => {
    expect(tableSortAria(createTable([{ id: 'name', desc: false }]), 'status')).toBeNull();
  });

  it('marks only the sorted column when another column is sortable', () => {
    const table = createTable([{ id: 'name', desc: true }]);

    expect(tableSortAria(table, 'name')).toBe('descending');
    expect(tableSortAria(table, 'created')).toBeNull();
  });

  it('omits the attribute for an unknown column', () => {
    expect(tableSortAria(createTable(), 'missing')).toBeNull();
  });
});
