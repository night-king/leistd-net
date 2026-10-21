import { MockException } from './models';
import { sortMockRows } from './sorting';

describe('mock property sorting', () => {
  const rows = [
    { id: 'b', rank: 10, active: false, time: 20 },
    { id: 'a', rank: 2, active: true, time: null },
    { id: 'c', rank: 2, active: false, time: 10 },
  ];
  const fields = {
    id: (row: (typeof rows)[number]) => row.id,
    rank: (row: (typeof rows)[number]) => row.rank,
    active: (row: (typeof rows)[number]) => row.active,
    'lastLogin.Time': (row: (typeof rows)[number]) => row.time,
  };

  it('honors all caller keys before tie breakers and compares numbers numerically', () => {
    const sorted = sortMockRows(rows, 'RANK asc, id DESCENDING', fields, 'rank', 'id');

    expect(sorted.map((row) => row.id)).toEqual(['c', 'a', 'b']);
    expect(rows.map((row) => row.id)).toEqual(['b', 'a', 'c']);
  });

  it('compares booleans and uses the stable key only for ties', () => {
    expect(sortMockRows(rows, 'active', fields, 'rank', 'id').map((row) => row.id)).toEqual([
      'b',
      'c',
      'a',
    ]);
  });

  it('matches PostgreSQL and memory LINQ null placement in both directions', () => {
    for (const [nulls, ascending, descending] of [
      ['last', ['c', 'b', 'a'], ['a', 'b', 'c']],
      ['first', ['a', 'c', 'b'], ['b', 'c', 'a']],
    ] as const) {
      expect(
        sortMockRows(rows, 'lastlogin.time asc', fields, 'rank', 'id', nulls).map((row) => row.id),
      ).toEqual(ascending);
      expect(
        sortMockRows(rows, 'lastLogin.Time desc', fields, 'rank', 'id', nulls).map((row) => row.id),
      ).toEqual(descending);
    }
  });

  it.each(['rank,', 'rank sideways', 'missing', '__proto__', 'constructor', 'id.toString()'])(
    'rejects invalid paths and syntax even with no rows: %s',
    (sorting) => {
      try {
        sortMockRows([], sorting, fields, 'rank', 'id');
        throw new Error('Expected invalid sorting to fail');
      } catch (error) {
        expect(error).toBeInstanceOf(MockException);
        expect((error as MockException).status).toBe(400);
        expect((error as MockException).error.code).toBeUndefined();
        expect((error as MockException).error.errors).toEqual([
          { field: 'sorting', detail: 'The sorting expression is not valid.' },
        ]);
      }
    },
  );

  it('uses the same default for omitted, blank and explicit sorting', () => {
    expect(sortMockRows(rows, undefined, fields, 'rank', 'id')).toEqual(
      sortMockRows(rows, 'rank asc', fields, 'rank', 'id'),
    );
    expect(sortMockRows(rows, '  ', fields, 'rank', 'id')).toEqual(
      sortMockRows(rows, 'rank', fields, 'rank', 'id'),
    );
  });
});
