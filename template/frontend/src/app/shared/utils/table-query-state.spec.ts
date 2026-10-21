import { convertToParamMap } from '@angular/router';
import { SortingState } from '@tanstack/angular-table';

import {
  DEFAULT_PAGE_SIZE,
  paginationFromQuery,
  sortingFromQuery,
  tableStateToQuery,
  toApiSorting,
} from './table-query-state';

const ALLOWED_COLUMNS = ['username', 'email', 'creationTime'] as const;
const FALLBACK_SORTING: SortingState = [{ id: 'username', desc: false }];

describe('table-query-state', () => {
  describe('paginationFromQuery', () => {
    it('reads page/pageSize into a zero-based PaginationState', () => {
      const params = convertToParamMap({ page: '3', pageSize: '50' });
      expect(paginationFromQuery(params)).toEqual({ pageIndex: 2, pageSize: 50 });
    });

    it('falls back to defaults when page/pageSize are missing', () => {
      expect(paginationFromQuery(convertToParamMap({}))).toEqual({
        pageIndex: 0,
        pageSize: DEFAULT_PAGE_SIZE,
      });
    });

    it('falls back to defaults for invalid page values', () => {
      const params = convertToParamMap({ page: 'abc', pageSize: '-5' });
      expect(paginationFromQuery(params)).toEqual({ pageIndex: 0, pageSize: DEFAULT_PAGE_SIZE });
    });

    it('rejects page sizes outside the allowed options', () => {
      const params = convertToParamMap({ page: '2', pageSize: '999' });
      expect(paginationFromQuery(params)).toEqual({ pageIndex: 1, pageSize: DEFAULT_PAGE_SIZE });
    });
  });

  describe('sortingFromQuery', () => {
    it('reads a whitelisted column with direction', () => {
      const params = convertToParamMap({ sort: 'email', direction: 'desc' });
      expect(sortingFromQuery(params, ALLOWED_COLUMNS, FALLBACK_SORTING)).toEqual([
        { id: 'email', desc: true },
      ]);
    });

    it('defaults to ascending when direction is absent', () => {
      const params = convertToParamMap({ sort: 'email' });
      expect(sortingFromQuery(params, ALLOWED_COLUMNS, FALLBACK_SORTING)).toEqual([
        { id: 'email', desc: false },
      ]);
    });

    it('falls back to the default sorting for unknown columns', () => {
      const params = convertToParamMap({ sort: 'password', direction: 'desc' });
      expect(sortingFromQuery(params, ALLOWED_COLUMNS, FALLBACK_SORTING)).toBe(FALLBACK_SORTING);
    });

    it('falls back to the default sorting when no sort is present', () => {
      expect(sortingFromQuery(convertToParamMap({}), ALLOWED_COLUMNS, FALLBACK_SORTING)).toBe(
        FALLBACK_SORTING,
      );
    });
  });

  describe('tableStateToQuery', () => {
    it('serializes a one-based page with sort/direction', () => {
      expect(
        tableStateToQuery({ pageIndex: 2, pageSize: 50 }, [{ id: 'email', desc: true }]),
      ).toEqual({ page: 3, pageSize: 50, sort: 'email', direction: 'desc' });
    });

    it('nulls out sort/direction when there is no active sort', () => {
      expect(tableStateToQuery({ pageIndex: 0, pageSize: 20 }, [])).toEqual({
        page: 1,
        pageSize: 20,
        sort: null,
        direction: null,
      });
    });

    it('round-trips through paginationFromQuery/sortingFromQuery', () => {
      const pagination = { pageIndex: 1, pageSize: 20 };
      const sorting: SortingState = [{ id: 'creationTime', desc: true }];
      const query = tableStateToQuery(pagination, sorting);
      const params = convertToParamMap({
        page: String(query['page']),
        pageSize: String(query['pageSize']),
        sort: query['sort'],
        direction: query['direction'],
      });
      expect(paginationFromQuery(params)).toEqual(pagination);
      expect(sortingFromQuery(params, ALLOWED_COLUMNS, FALLBACK_SORTING)).toEqual(sorting);
    });
  });

  describe('toApiSorting', () => {
    it('serializes all sorting keys in caller order', () => {
      expect(
        toApiSorting([
          { id: 'creationTime', desc: true },
          { id: 'username', desc: false },
        ]),
      ).toBe('creationTime desc, username asc');
    });

    it('formats the active sort as "field dir"', () => {
      expect(toApiSorting([{ id: 'email', desc: true }])).toBe('email desc');
      expect(toApiSorting([{ id: 'username', desc: false }])).toBe('username asc');
    });

    it('returns undefined when there is no active sort', () => {
      expect(toApiSorting([])).toBeUndefined();
    });
  });
});
