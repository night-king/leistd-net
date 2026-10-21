import { Directive, ElementRef, effect, inject, input } from '@angular/core';

/**
 * 给 Spartan Popover 生成的对话框补可访问名：Brain 把 `role="dialog"` 设在 CDK overlay pane 上且不透传
 * aria，本指令把名称写到该 pane 上；Spartan 原生支持后可移除。
 * 用法：`<hlm-popover-content *hlmPopoverPortal [appPopoverAria]="title()">`
 */
@Directive({
  selector: '[appPopoverAria]',
})
export class PopoverAria {
  /** 对话框的可访问名（通常与面板内可见标题一致）。 */
  readonly label = input.required<string>({ alias: 'appPopoverAria' });

  private readonly host = inject(ElementRef<HTMLElement>);

  constructor() {
    effect(() => {
      const label = this.label();
      const pane = (this.host.nativeElement as HTMLElement).closest<HTMLElement>(
        '.cdk-overlay-pane',
      );
      if (!pane || !label) {
        return;
      }
      pane.setAttribute('aria-label', label);
    });
  }
}
