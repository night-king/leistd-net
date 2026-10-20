/// <reference types="@vitest/browser-playwright" />
import { userEvent } from 'vitest/browser';

/**
 * 把物理指针停到页面右上角的空白处。真实 Chromium 里指针位置会跨用例保留：上一个用例悬停过的位置上
 * 若出现新的元素，它会在准备阶段就收到 pointerenter（如提示提前打开）。测试焦点或键盘行为之前先移开，
 * 并由调用方确认准备期没有留下浮层。
 */
export async function parkPointer(): Promise<void> {
  await userEvent.unhover(document.body, {
    position: { x: document.body.clientWidth - 1, y: 0 },
  });
}
