import { inject, Injectable } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { HlmDialogService } from '@spartan-ng/helm/dialog';
import { firstValueFrom } from 'rxjs';

import { ConfirmContext, ConfirmDialog } from './confirm-dialog';

export interface ConfirmOptions {
  message: string;
  /** 标题（默认「请确认」）。 */
  header?: string;
  /** 确认按钮文案（默认「确定」）。 */
  confirmText?: string;
  /** 取消按钮文案（默认「取消」）。 */
  cancelText?: string;
  /** 确认按钮样式；破坏性操作（删除等）用 destructive。 */
  variant?: 'default' | 'destructive';
}

/**
 * 确认对话框服务：基于 `HlmDialogService` 动态打开通用 confirm 组件，确认时返回 `true`。
 * 用法：`if (await confirm.open({ message, variant: 'destructive' })) { ... }`。
 */
@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private readonly dialog = inject(HlmDialogService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#endif

  async open(options: ConfirmOptions): Promise<boolean> {
    const context: ConfirmContext = {
      message: options.message,
      //#if (IncludeLocalization)
      header: options.header ?? this.transloco.translate('common.confirm'),
      confirmText: options.confirmText ?? this.transloco.translate('common.ok'),
      cancelText: options.cancelText ?? this.transloco.translate('common.cancel'),
      //#else
      header: options.header ?? 'Please confirm',
      confirmText: options.confirmText ?? 'OK',
      cancelText: options.cancelText ?? 'Cancel',
      //#endif
      variant: options.variant ?? 'default',
    };

    // role=alertdialog + aria 关联标题/正文：确认/破坏性提示的 WAI-ARIA 正确语义。
    // 无 ✕ 关闭钮（alert-dialog 语义：必须显式选择动作）；宽度用面板默认 sm:max-w-sm。
    const dialogRef = this.dialog.open<boolean, ConfirmContext>(ConfirmDialog, {
      context,
      showCloseButton: false,
      role: 'alertdialog',
      ariaLabelledBy: 'confirm-dialog-title',
      ariaDescribedBy: 'confirm-dialog-description',
    });

    const result = await firstValueFrom(dialogRef.closed$);
    return result === true;
  }
}
