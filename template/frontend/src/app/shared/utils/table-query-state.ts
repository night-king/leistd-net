import { ParamMap, Params } from '@angular/router';
import { PaginationState, SortingState, Updater } from '@tanstack/angular-table';

export const PAGE_SIZE_OPTIONS = [10, 20, 50, 100] as const;
export const DEFAULT_PAGE_SIZE = 20;

export function paginationFromQuery(params: ParamMap): PaginationState {
  const page = readPositiveInteger(params.get('page'), 1);
  const requestedPageSize = readPositiveInteger(params.get('pageSize'), DEFAULT_PAGE_SIZE);
  const pageSize = PAGE_SIZE_OPTIONS.includes(
    requestedPageSize as (typeof PAGE_SIZE_OPTIONS)[number],
  )
    ? requestedPageSize
    : DEFAULT_PAGE_SIZE;

  return { pageIndex: page - 1, pageSize };
}

export function sortingFromQuery(
  params: ParamMap,
  allowedColumns: readonly string[],
  fallback: SortingState,
): SortingState {
  const id = params.get('sort');
  if (!id || !allowedColumns.includes(id)) {
    return fallback;
  }

  return [{ id, desc: params.get('direction') === 'desc' }];
}

export function tableStateToQuery(pagination: PaginationState, sorting: SortingState): Params {
  const activeSort = sorting[0];
  return {
    page: pagination.pageIndex + 1,
    pageSize: pagination.pageSize,
    sort: activeSort?.id ?? null,
    direction: activeSort ? (activeSort.desc ? 'desc' : 'asc') : null,
  };
}

export function toApiSorting(sorting: SortingState): string | undefined {
  return sorting.length
    ? sorting.map((item) => `${item.id} ${item.desc ? 'desc' : 'asc'}`).join(', ')
    : undefined;
}

export function resolveTableUpdater<T>(updater: Updater<T>, current: T): T {
  return updater instanceof Function ? updater(current) : updater;
}

function readPositiveInteger(value: string | null, fallback: number): number {
  const parsed = Number.parseInt(value ?? '', 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}
