import { timeZoneGroups, timeZoneMatches, TimeZoneOption } from './setting-choices';

/**
 * 时区清单的合法性按能否渲染判定，不按 `Intl.supportedValuesOf('timeZone')` 过滤：它只列规范名，
 * 不含 `UTC`、`Asia/Kolkata` 等可用值。
 */
describe('time zone choices', () => {
  const groups = timeZoneGroups();
  const all: TimeZoneOption[] = groups.flatMap((group) => [...group.zones]);
  const find = (id: string) => all.find((zone) => zone.value === id);

  it('contains only zones that can actually be used for formatting', () => {
    expect(all.length).toBeGreaterThan(100);
    for (const zone of all) {
      expect(() =>
        new Intl.DateTimeFormat('en', { timeZone: zone.value }).format(new Date()),
      ).not.toThrow();
    }
  });

  // UTC 不在 supportedValuesOf 里，却是最常见的运维选择：它缺席就是个 bug。
  it('includes UTC', () => {
    expect(find('UTC')).toBeTruthy();
  });

  it('includes common regions and finds renamed zones by either spelling', () => {
    expect(find('Asia/Shanghai')).toBeTruthy();

    // 印度在多数运行时的规范名是 Asia/Calcutta，但人会去搜 Kolkata
    const india = all.find((zone) => /Calcutta|Kolkata/.test(zone.value));
    expect(india).toBeTruthy();
    expect(timeZoneMatches(india!, 'Kolkata')).toBe(true);
    expect(timeZoneMatches(india!, 'Calcutta')).toBe(true);
  });

  // 同一个时区列成两行，选哪个都一样，只会让人怀疑自己选错了。
  it('lists each time zone only once', () => {
    expect(all.length).toBe(new Set(all.map((zone) => zone.value)).size);
  });

  it('always includes the browser time zone and marks it', () => {
    const browser = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const marked = all.filter((zone) => zone.isBrowser);

    expect(marked.length).toBe(1);
    expect(
      new Intl.DateTimeFormat('en', { timeZone: marked[0].value }).resolvedOptions().timeZone,
    ).toBe(new Intl.DateTimeFormat('en', { timeZone: browser }).resolvedOptions().timeZone);
  });

  it('returns non-empty groups sorted by region name', () => {
    expect(groups.length).toBeGreaterThan(1);
    for (const group of groups) {
      expect(group.zones.length).toBeGreaterThan(0);
    }
    expect(groups.map((group) => group.region)).toEqual(
      [...groups.map((group) => group.region)].sort((a, b) => a.localeCompare(b)),
    );
  });

  it('shows offsets in UTC notation and matches GMT notation in search', () => {
    const shanghai = find('Asia/Shanghai')!;

    expect(shanghai.offsetLabel).toBe('UTC+8');
    expect(timeZoneMatches(shanghai, 'UTC+8')).toBe(true);
    expect(timeZoneMatches(shanghai, 'GMT+8')).toBe(true);
  });

  it('matches place names and full IANA names but not unrelated terms', () => {
    const shanghai = find('Asia/Shanghai')!;

    expect(timeZoneMatches(shanghai, 'shang')).toBe(true);
    expect(timeZoneMatches(shanghai, 'Asia/Shang')).toBe(true);
    expect(timeZoneMatches(shanghai, '  ')).toBe(true);
    expect(timeZoneMatches(shanghai, 'reykjavik')).toBe(false);
  });
});
