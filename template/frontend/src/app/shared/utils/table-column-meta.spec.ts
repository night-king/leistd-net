import { ColumnDef } from '@tanstack/angular-table';

import { tableColumnVisibility } from './table-column-meta';

import type { AppTableFeatures } from './table-features';

interface TestRow {
  id: string;
  summary: string;
  detail: string;
  actions: string;
}

describe('tableColumnVisibility', () => {
  const columns: ColumnDef<AppTableFeatures, TestRow>[] = [
    { accessorKey: 'id', id: 'id', meta: { priority: 'primary' } },
    { accessorKey: 'summary', id: 'summary', meta: { priority: 'secondary' } },
    { accessorKey: 'detail', id: 'detail', meta: { priority: 'tertiary' } },
    {
      accessorKey: 'actions',
      id: 'actions',
      meta: { priority: 'tertiary', locked: true },
    },
  ];

  it('shows primary and locked columns on mobile', () => {
    expect(tableColumnVisibility(columns, 'mobile')).toEqual({
      id: true,
      summary: false,
      detail: false,
      actions: true,
    });
  });

  it('adds secondary columns on tablet', () => {
    expect(tableColumnVisibility(columns, 'tablet')).toEqual({
      id: true,
      summary: true,
      detail: false,
      actions: true,
    });
  });

  it('shows all columns on desktop', () => {
    expect(tableColumnVisibility(columns, 'desktop')).toEqual({
      id: true,
      summary: true,
      detail: true,
      actions: true,
    });
  });
});
