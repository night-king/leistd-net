/// <reference types="@vitest/browser-playwright" />
import { userEvent } from 'vitest/browser';

/**
 * 把物理指针移出测试内容区。Vitest 不重置物理指针，它停在之前悬停过的位置：之后在那里出现的元素
 * 会收到 pointerenter，提示可能提前打开，或因"悬停在提示上"而关不掉。合成指针事件不移动物理指针，
 * 代替不了这一步。内容区之外不会出现被测元素，所以可在渲染前（如 beforeEach）调用；
 * force 跳过"该点须命中 body"的检查，只移动指针，不点击、不改焦点。
 */
export async function parkPointer(): Promise<void> {
  await userEvent.unhover(document.body, { position: { x: -1, y: -1 }, force: true });
}
