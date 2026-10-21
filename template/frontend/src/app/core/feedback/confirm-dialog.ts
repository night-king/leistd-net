import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideTriangleAlert } from '@ng-icons/lucide';
import { BrnDialogRef, injectBrnDialogContext } from '@spartan-ng/brain/dialog';
import { HlmButton } from '@spartan-ng/helm/button';

/** confirm 对话框的上下文（由 ConfirmService 传入）。 */
export interface ConfirmContext {
  message: string;
  header: string;
  confirmText: string;
  cancelText: string;
  /** 确认按钮样式：destructive 用于删除等破坏性操作。 */
  variant: 'default' | 'destructive';
}

/**
 * 通用确认对话框内容组件，经 HlmDialogService 动态打开。
 * 关闭时通过 BrnDialogRef 回传布尔结果（确认 = true）。
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, NgIcon],
  providers: [provideIcons({ lucideTriangleAlert })],
  // 结构对齐 Spartan alert-dialog 默认解剖：media 图标块 + header + muted footer 条。
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex items-start gap-4">
        <div class="bg-muted inline-flex size-10 shrink-0 items-center justify-center rounded-md">
          <ng-icon name="lucideTriangleAlert" class="text-destructive text-2xl" />
        </div>
        <div class="flex flex-col gap-1.5">
          <h2 id="confirm-dialog-title" class="text-base font-medium">{{ ctx.header }}</h2>
          <p id="confirm-dialog-description" class="text-muted-foreground text-sm text-balance">
            {{ ctx.message }}
          </p>
        </div>
      </div>
      <div
        class="bg-muted/50 -mx-4 -mb-4 flex flex-col-reverse gap-2 rounded-b-xl border-t p-4 sm:flex-row sm:justify-end"
      >
        <button hlmBtn variant="outline" (click)="close(false)">{{ ctx.cancelText }}</button>
        <button hlmBtn [variant]="ctx.variant" (click)="close(true)">{{ ctx.confirmText }}</button>
      </div>
    </div>
  `,
})
export class ConfirmDialog {
  protected readonly ctx = injectBrnDialogContext<ConfirmContext>();
  private readonly dialogRef = inject<BrnDialogRef<boolean>>(BrnDialogRef);

  close(result: boolean): void {
    this.dialogRef.close(result);
  }
}
