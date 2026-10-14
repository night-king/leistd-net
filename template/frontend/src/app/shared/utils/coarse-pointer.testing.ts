/// <reference types="@vitest/browser-playwright" />
import { cdp } from 'vitest/browser';

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
 * 断言元素是一个可点的触屏目标：宽高都不小于 44px，且四条边中点附近与中心这几个采样点的点按都落在它自己身上，
 * 没有被祖先裁掉或被别的元素盖住。不取四角：命中检测按圆角裁剪，角上的点本来就落在元素外。
 * 命中检测只认视口内的点，先把元素滚到视口中间。
 */
export function expectTouchTarget(element: Element): void {
  element.scrollIntoView({ block: 'center', inline: 'center' });
  const rect = element.getBoundingClientRect();
  // 只容忍布局取整的浮点误差，不放宽到半个像素
  const tolerance = 0.01;
  expect(rect.width + tolerance, 'touch target width').toBeGreaterThanOrEqual(MIN_TOUCH_TARGET_PX);
  expect(rect.height + tolerance, 'touch target height').toBeGreaterThanOrEqual(
    MIN_TOUCH_TARGET_PX,
  );
  const centerX = rect.left + rect.width / 2;
  const centerY = rect.top + rect.height / 2;
  const points = [
    [rect.left + 2, centerY],
    [rect.right - 2, centerY],
    [centerX, rect.top + 2],
    [centerX, rect.bottom - 2],
    [centerX, centerY],
  ];
  for (const [x, y] of points) {
    const hit = document.elementFromPoint(x, y);
    expect(hit !== null && (hit === element || element.contains(hit)), `hit at ${x},${y}`).toBe(
      true,
    );
  }
}
