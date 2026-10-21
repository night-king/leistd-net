import { TestBed } from '@angular/core/testing';

import { COPIED_FEEDBACK_MS, injectCopyToClipboard } from './clipboard';

import type { Mock } from 'vitest';

describe('injectCopyToClipboard', () => {
  let writeText: Mock;

  beforeEach(() => {
    writeText = vi.fn().mockName('writeText').mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    vi.useFakeTimers();
  });

  afterEach(() => vi.useRealTimers());

  const create = () => TestBed.runInInjectionContext(() => injectCopyToClipboard());

  it('copies the text and shows the copied state for a short while', async () => {
    const clipboard = create();

    expect(await clipboard.copy('secret')).toBe(true);
    expect(writeText).toHaveBeenCalledWith('secret');
    expect(clipboard.copied()).toBe(true);

    vi.advanceTimersByTime(COPIED_FEEDBACK_MS);
    expect(clipboard.copied()).toBe(false);
  });

  // 非安全上下文或权限被拒：不抛出、不显示已复制，调用方据返回值决定提示
  it('reports failure without throwing when the clipboard is unavailable', async () => {
    writeText.mockRejectedValue(new DOMException('Denied', 'NotAllowedError'));
    const clipboard = create();

    expect(await clipboard.copy('secret')).toBe(false);
    expect(clipboard.copied()).toBe(false);
  });
});
