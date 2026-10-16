/** 标签之间至少留出的空白（px）。 */
const LABEL_GAP_PX = 12;

/**
 * 估算一行标签的宽度（px）。中日韩字符按一个字号宽，其余按 0.62 个字号宽（日期、钟点这类数字与连字符的常见宽度）。
 * 只是决定刻度步长的估算预算，不是任意字体的宽度上界；图表库的碰撞去重仍作兜底。
 */
export function estimateLabelWidth(text: string, fontSize: number): number {
  let width = 0;
  for (const char of text) {
    width += isWide(char.codePointAt(0) ?? 0) ? fontSize : fontSize * 0.62;
  }
  return width;
}

/** 全宽字符：CJK 符号与标点、假名、汉字（0x3000–0x9fff），谚文音节，全角形式。 */
function isWide(code: number): boolean {
  return (
    (code >= 0x3000 && code <= 0x9fff) ||
    (code >= 0xac00 && code <= 0xd7af) ||
    (code >= 0xff00 && code <= 0xffef)
  );
}

export interface EvenCategoryTicksOptions {
  /** 横轴可用宽度（px）：首个点到最后一个点之间的距离。 */
  axisWidth: number;
  /** 刻度标签字号（px）。 */
  fontSize: number;
  /**
   * 候选步长（每隔几个点放一个刻度），从小到大。时间轴取人熟悉的间隔：按日 1、2、3、7、14，按小时 1、2、3、4、6、12。
   * 都放不下时退回到能放下的最小整数步长。
   */
  steps?: readonly number[];
}

/**
 * 类目轴（每个时段一个点）上等间隔取刻度：所有刻度之间隔相同的点数，步长取候选里能放下最宽标签的最小值，
 * 从最后一个点往前数，保证最近的时段有标签。
 *
 * 不只靠图表库：类目比例尺把每个点都当刻度候选，碰撞去重（`thin`）只保证标签不重叠、不保证留下的刻度等间隔，
 * 间隔忽大忽小读起来像数据有缺口或时间不均匀。
 *
 * @param values 每个点的值（与横轴比例尺的定义域同序）。
 * @param labels 每个点的标签文本，与 `values` 一一对应。
 */
export function evenCategoryTicks<T>(
  values: readonly T[],
  labels: readonly string[],
  options: EvenCategoryTicksOptions,
): T[] {
  const count = values.length;
  if (count <= 1) {
    return [...values];
  }
  // 没有可用宽度时只标最近一个点
  if (options.axisWidth <= 0) {
    return [values[count - 1]];
  }

  const widest = Math.max(...labels.map((label) => estimateLabelWidth(label, options.fontSize)));
  const pointSpacing = options.axisWidth / (count - 1);
  // 相邻两个刻度的中心距至少要容下一个标签加间隙；点距按实际值算，长区间窄图里它可以小于 1px
  const minimum = Math.max(1, Math.ceil((widest + LABEL_GAP_PX) / pointSpacing));
  const step = options.steps?.find((candidate) => candidate >= minimum) ?? minimum;

  const ticks: T[] = [];
  for (let index = count - 1; index >= 0; index -= step) {
    ticks.unshift(values[index]);
  }
  return ticks;
}
