import { estimateLabelWidth, evenCategoryTicks } from './category-ticks';

/** 从 09-17 起连续 n 天的标签，值即标签。 */
function days(count: number): string[] {
  return Array.from({ length: count }, (_, index) => {
    const date = new Date(Date.UTC(2026, 8, 17 + index));
    const month = String(date.getUTCMonth() + 1).padStart(2, '0');
    const day = String(date.getUTCDate()).padStart(2, '0');
    return `${month}-${day}`;
  });
}

/** 刻度在原序列里的下标间隔。 */
function gaps(values: readonly string[], ticks: readonly string[]): number[] {
  const indexes = ticks.map((tick) => values.indexOf(tick));
  return indexes.slice(1).map((index, position) => index - indexes[position]);
}

describe('evenCategoryTicks', () => {
  const daySteps = [1, 2, 3, 7, 14];

  it('spaces ticks evenly and always labels the latest point', () => {
    const values = days(30);
    const ticks = evenCategoryTicks(values, values, {
      axisWidth: 1140,
      fontSize: 11,
      steps: daySteps,
    });

    expect(ticks.at(-1)).toBe('10-16');
    expect(new Set(gaps(values, ticks)).size).toBe(1);
  });

  it('takes the smallest candidate step that keeps the labels apart', () => {
    const values = days(30);
    // 29 段、每段 39.3px：一个 11px 的 "09-17" 约 34px，加 12px 间隙放不进一段，两段够
    const ticks = evenCategoryTicks(values, values, {
      axisWidth: 1140,
      fontSize: 11,
      steps: daySteps,
    });
    expect(gaps(values, ticks)[0]).toBe(2);

    // 窄到两段放不下、三段放得下（每段 20.7px）
    const narrow = evenCategoryTicks(values, values, {
      axisWidth: 600,
      fontSize: 11,
      steps: daySteps,
    });
    expect(gaps(values, narrow)[0]).toBe(3);

    // 再窄（每段 13.8px，至少要 4 段）就跳到整周，不取 4、5、6 这类不常用的间隔
    const narrower = evenCategoryTicks(values, values, {
      axisWidth: 400,
      fontSize: 11,
      steps: daySteps,
    });
    expect(gaps(values, narrower)[0]).toBe(7);
  });

  it('never lets two neighbouring labels overlap', () => {
    const values = days(30);
    for (const axisWidth of [320, 480, 700, 960, 1140, 1600]) {
      const ticks = evenCategoryTicks(values, values, { axisWidth, fontSize: 11, steps: daySteps });
      const distance =
        (gaps(values, ticks)[0] ?? values.length) * (axisWidth / (values.length - 1));
      expect(distance, `axis ${axisWidth}px`).toBeGreaterThanOrEqual(
        estimateLabelWidth('09-17', 11),
      );
    }
  });

  // 一年按日、窄图：点距不到 1px，不能当 1px 算，否则选出的步长放不下标签，碰撞去重后间隔又不均匀
  it('keeps labels apart when the points are less than a pixel apart', () => {
    const values = Array.from({ length: 366 }, (_, index) => `d${String(index).padStart(3, '0')}`);
    const labels = values.map(() => '09-17');
    const axisWidth = 244;
    const ticks = evenCategoryTicks(values, labels, {
      axisWidth,
      fontSize: 10,
      steps: [1, 2, 3, 7, 14],
    });

    expect(ticks.at(-1)).toBe('d365');
    expect(new Set(gaps(values, ticks)).size).toBe(1);
    const distance = gaps(values, ticks)[0] * (axisWidth / (values.length - 1));
    expect(distance).toBeGreaterThanOrEqual(estimateLabelWidth('09-17', 10) + 12);
  });

  it('labels only the latest point when there is no axis width', () => {
    const values = days(30);
    expect(evenCategoryTicks(values, values, { axisWidth: 0, fontSize: 11 })).toEqual(['10-16']);
  });

  it('falls back to the smallest step that fits when no candidate is wide enough', () => {
    const values = days(30);
    const ticks = evenCategoryTicks(values, values, {
      axisWidth: 120,
      fontSize: 11,
      steps: [1, 2],
    });

    expect(new Set(gaps(values, ticks)).size).toBe(1);
    expect(gaps(values, ticks)[0]).toBeGreaterThan(2);
    expect(ticks.at(-1)).toBe('10-16');
  });

  it('labels every point when there is room', () => {
    const values = days(7);
    expect(
      evenCategoryTicks(values, values, { axisWidth: 1000, fontSize: 11, steps: daySteps }),
    ).toEqual(values);
  });

  it('returns a single point or none unchanged', () => {
    expect(evenCategoryTicks(['10-16'], ['10-16'], { axisWidth: 10, fontSize: 11 })).toEqual([
      '10-16',
    ]);
    expect(evenCategoryTicks([], [], { axisWidth: 10, fontSize: 11 })).toEqual([]);
  });

  it('measures CJK characters a full font size wide', () => {
    expect(estimateLabelWidth('十月', 12)).toBe(24);
    expect(estimateLabelWidth('10-16', 10)).toBeCloseTo(31);
  });
});
