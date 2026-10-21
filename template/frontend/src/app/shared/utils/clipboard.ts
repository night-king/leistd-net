import { DestroyRef, inject, Signal, signal } from '@angular/core';

/** 复制成功后「已复制」状态保持的时长。 */
export const COPIED_FEEDBACK_MS = 2000;

/** 复制到剪贴板的动作，附带一小段「已复制」状态。 */
export interface CopyToClipboard {
  /** 刚复制成功后的 {@link COPIED_FEEDBACK_MS} 内为 `true`，供按钮切成「已复制」。 */
  readonly copied: Signal<boolean>;
  /** 复制文本；剪贴板不可用（非安全上下文、权限被拒）时返回 `false` 而不抛出。 */
  copy(text: string): Promise<boolean>;
}

/** 在注入上下文里创建复制动作；「已复制」计时器随组件销毁清理，连点时重新计时。 */
export function injectCopyToClipboard(): CopyToClipboard {
  const copied = signal(false);
  let timer: ReturnType<typeof setTimeout> | undefined;
  inject(DestroyRef).onDestroy(() => clearTimeout(timer));

  return {
    copied: copied.asReadonly(),
    async copy(text: string): Promise<boolean> {
      try {
        await navigator.clipboard.writeText(text);
      } catch {
        return false;
      }

      copied.set(true);
      clearTimeout(timer);
      timer = setTimeout(() => copied.set(false), COPIED_FEEDBACK_MS);
      return true;
    },
  };
}
