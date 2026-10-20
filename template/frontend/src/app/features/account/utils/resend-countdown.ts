import { DestroyRef, inject, signal } from '@angular/core';

/**
 * 验证码的重发倒计时：按服务端给的间隔（`retryAfterSeconds`）逐秒倒数到 0，组件销毁时停表。
 * 须在注入上下文里创建（字段初始化或构造函数）。
 */
export function resendCountdown() {
  const remaining = signal(0);
  let timer: ReturnType<typeof setInterval> | undefined;

  const stop = () => {
    clearInterval(timer);
    timer = undefined;
    remaining.set(0);
  };
  inject(DestroyRef).onDestroy(stop);

  return {
    /** 剩余秒数；0 表示可以重发。 */
    remaining: remaining.asReadonly(),
    start(seconds: number): void {
      stop();
      remaining.set(Math.max(0, Math.ceil(seconds)));
      if (remaining() > 0) {
        timer = setInterval(() => {
          const next = remaining() - 1;
          if (next <= 0) {
            stop();
          } else {
            remaining.set(next);
          }
        }, 1000);
      }
    },
    stop,
  };
}
