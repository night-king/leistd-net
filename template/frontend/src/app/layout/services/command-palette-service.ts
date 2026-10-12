import { Injectable, signal } from '@angular/core';

/**
 * 命令面板的开合状态。侧栏与顶栏的搜索入口、⌘K / Ctrl+K 都写这一处，面板本身由布局各放一个。
 */
@Injectable({ providedIn: 'root' })
export class CommandPaletteService {
  private readonly opened = signal(false);

  readonly isOpen = this.opened.asReadonly();

  open(): void {
    this.opened.set(true);
  }

  close(): void {
    this.opened.set(false);
  }

  toggle(): void {
    this.opened.update((opened) => !opened);
  }
}
