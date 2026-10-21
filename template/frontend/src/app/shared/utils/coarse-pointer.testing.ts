/// <reference types="@vitest/browser-playwright" />
import { cdp, userEvent } from 'vitest/browser';

/** 触屏点按目标的最小边长（docs/standards/frontend-ui.md「可访问性」）。 */
export const MIN_TOUCH_TARGET_PX = 44;

/**
 * 切换当前测试页面是否按触屏设备运行。经 CDP 改变浏览器的输入设备能力，`(pointer: coarse)` 随之成立，
 * styles.css 与组件上的 pointer-coarse 规则由浏览器按原有级联真实套用；不替换 matchMedia。
 * 模拟作用于本 worker 的整个页面，同一页面随后还要运行别的测试文件：开启后必须在 afterEach 里 await 关闭。
 */
export async function setCoarsePointer(enabled: boolean): Promise<void> {
  // maxTouchPoints 在关闭时也必须是 1–16
  await cdp().send('Emulation.setTouchEmulationEnabled', { enabled, maxTouchPoints: 5 });
  if (matchMedia('(pointer: coarse)').matches !== enabled) {
    throw new Error(`Touch emulation did not switch (pointer: coarse) to ${enabled}.`);
  }
}

/**
 * 断言元素是一个可点的触屏目标：有效命中框宽高都不小于 44px，且四条边中点与中心的点按都落在它自己身上，
 * 没有被祖先裁掉或被别的元素盖住。不取四角：命中检测按圆角裁剪，角上的点本来就落在元素外。
 *
 * 元素自身不足 44px 时，命中区必须来自 styles.css 铺开的透明 `::after`：以元素中心为心、按伪元素的计算尺寸
 * 构造有效命中框，在中心和四边中点内缩 0.25px 处采样，用来发现裁切（包括 `overflow-*` 滚动容器裁掉伸出
 * 它的部分）与邻近目标覆盖；这是采样检查，结果也受浏览器命中检测的取整影响，不保证不足一像素的裁切都能查出。
 * 命中检测只认视口内的点，先把元素滚到视口中间。几何只说明点按落在了谁身上，点按是否触发了动作由 `tapAt` 加行为断言证明。
 */
export function expectTouchTarget(element: Element): void {
  element.scrollIntoView({ block: 'center', inline: 'center' });
  const box = touchTargetBox(element);
  // 只容忍布局取整的浮点误差，不放宽到半个像素
  const tolerance = 0.01;
  expect(box.width + tolerance, 'touch target width').toBeGreaterThanOrEqual(MIN_TOUCH_TARGET_PX);
  expect(box.height + tolerance, 'touch target height').toBeGreaterThanOrEqual(MIN_TOUCH_TARGET_PX);
  const inset = box.expanded ? 0.25 : 2;
  const centerX = box.left + box.width / 2;
  const centerY = box.top + box.height / 2;
  const points = [
    [box.left + inset, centerY],
    [box.left + box.width - inset, centerY],
    [centerX, box.top + inset],
    [centerX, box.top + box.height - inset],
    [centerX, centerY],
  ];
  for (const [x, y] of points) {
    const hit = document.elementFromPoint(x, y);
    expect(hit !== null && (hit === element || element.contains(hit)), `hit at ${x},${y}`).toBe(
      true,
    );
  }
}

/**
 * 在以元素中心为原点、偏移 (dx, dy) 的视口坐标上真实点按一次：浏览器按坐标做命中检测再派发事件，
 * 落在伪元素铺开的区域也算点到元素。不用 `HTMLElement.click()`，那会绕过命中检测。
 * 点禁用控件时传 `disabled`：先确认该坐标确实落在控件上，再跳过 Playwright 的可点前提照样按下，
 * 用来证明扩出的命中区没有绕过禁用，而不是点在了空白处。
 */
export async function tapAt(
  element: Element,
  dx: number,
  dy: number,
  options: { readonly disabled?: boolean } = {},
): Promise<void> {
  element.scrollIntoView({ block: 'center', inline: 'center' });
  const rect = element.getBoundingClientRect();
  if (options.disabled) {
    const hit = document.elementFromPoint(
      rect.left + rect.width / 2 + dx,
      rect.top + rect.height / 2 + dy,
    );
    expect(
      hit !== null && (hit === element || element.contains(hit)),
      'tap lands on the control',
    ).toBe(true);
  }
  // Playwright 的 position 以 padding box 左上角为原点，getBoundingClientRect 是 border box
  const style = getComputedStyle(element);
  await userEvent.click(element, {
    position: {
      x: rect.width / 2 + dx - Number.parseFloat(style.borderLeftWidth),
      y: rect.height / 2 + dy - Number.parseFloat(style.borderTopWidth),
    },
    force: options.disabled,
  });
}

interface TouchTargetBox {
  readonly left: number;
  readonly top: number;
  readonly width: number;
  readonly height: number;
  /** 命中区来自 `::after`，不是元素自身的盒子。 */
  readonly expanded: boolean;
}

function touchTargetBox(element: Element): TouchTargetBox {
  const rect = element.getBoundingClientRect();
  if (rect.width >= MIN_TOUCH_TARGET_PX && rect.height >= MIN_TOUCH_TARGET_PX) {
    return {
      left: rect.left,
      top: rect.top,
      width: rect.width,
      height: rect.height,
      expanded: false,
    };
  }
  const after = getComputedStyle(element, '::after');
  expect(
    after.content !== 'none' && after.content !== 'normal' && after.position === 'absolute',
    `${describe(element)} is ${rect.width}x${rect.height}px and needs an absolutely positioned ::after hit area`,
  ).toBe(true);
  const width = Math.max(rect.width, Number.parseFloat(after.width));
  const height = Math.max(rect.height, Number.parseFloat(after.height));
  return {
    left: rect.left + rect.width / 2 - width / 2,
    top: rect.top + rect.height / 2 - height / 2,
    width,
    height,
    expanded: true,
  };
}

function describe(element: Element): string {
  const label = element.getAttribute('aria-label') ?? element.id;
  return `<${element.tagName.toLowerCase()}${label ? ` ${label}` : ''}>`;
}
