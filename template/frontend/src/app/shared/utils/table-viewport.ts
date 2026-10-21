import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { inject, Signal, computed } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';

import { TableViewport } from './table-column-meta';

/** 表格列可见性的两个断点，与 Tailwind 的 `md` / `lg` 对齐。 */
const MEDIUM_VIEWPORT = '(min-width: 768px)';
const LARGE_VIEWPORT = '(min-width: 1024px)';

/**
 * 当前视口档位（mobile / tablet / desktop），须在注入上下文里调用；带吸附操作列的表格只把它当上限，
 * 容器放不下时由 `TableFit` 再降一档。
 */
export function tableViewportSignal(): Signal<TableViewport> {
  const breakpointObserver = inject(BreakpointObserver);

  const state = toSignal(breakpointObserver.observe([MEDIUM_VIEWPORT, LARGE_VIEWPORT]), {
    initialValue: {
      matches: false,
      breakpoints: { [MEDIUM_VIEWPORT]: false, [LARGE_VIEWPORT]: false },
    } satisfies BreakpointState,
  });

  return computed(() => {
    const breakpoints = state().breakpoints;
    if (breakpoints[LARGE_VIEWPORT] === true) return 'desktop';
    if (breakpoints[MEDIUM_VIEWPORT] === true) return 'tablet';
    return 'mobile';
  });
}
