import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  signal,
} from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideInfo } from '@ng-icons/lucide';
import { provideBrnPopoverDefaultOptions } from '@spartan-ng/brain/popover';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmPopoverImports } from '@spartan-ng/helm/popover';

import { PopoverAria } from '../../directives/popover-aria';

/** 指针离开按钮与说明后多久收起：够跨过两者之间的间隙。 */
const CLOSE_DELAY_MS = 150;

/**
 * 标题、标签旁的补充说明：一个信息图标，鼠标悬停、点按（触屏同样）都展开说明，Escape 或点别处收起。
 *
 * 用 Popover 而不是 Tooltip：Tooltip 只响应鼠标与聚焦，触屏点按打不开。说明是纯文字，展开时不抢焦点。
 * 按钮在粗指针下按项目约定放大到 44px，负外边距让它不撑高所在的标题行。
 */
@Component({
  selector: 'app-info-hint',
  imports: [NgIcon, HlmButton, PopoverAria, ...HlmPopoverImports],
  providers: [provideIcons({ lucideInfo }), provideBrnPopoverDefaultOptions({ autoFocus: false })],
  templateUrl: './info-hint.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InfoHint {
  /** 说明文字，同时是按钮与浮层的可访问名。 */
  readonly text = input.required<string>();

  protected readonly state = signal<'open' | 'closed'>('closed');

  private closeTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.cancelClose());
  }

  /**
   * 鼠标与触控笔悬停即展开；按钮与说明算同一个阅读区域，离开两者稍等再收起，指针才能跨过间隙移到说明上读。
   * 触摸没有悬停，交给点按。
   */
  protected hover(event: PointerEvent, entering: boolean): void {
    if (event.pointerType !== 'mouse' && event.pointerType !== 'pen') {
      return;
    }

    this.cancelClose();
    if (entering) {
      this.state.set('open');
    } else {
      this.closeTimer = setTimeout(() => this.state.set('closed'), CLOSE_DELAY_MS);
    }
  }

  /** 浮层自己收起（Escape、点别处、再次点按）时丢掉待执行的收起。 */
  protected onStateChanged(state: 'open' | 'closed'): void {
    this.cancelClose();
    this.state.set(state);
  }

  private cancelClose(): void {
    clearTimeout(this.closeTimer);
    this.closeTimer = undefined;
  }
}
