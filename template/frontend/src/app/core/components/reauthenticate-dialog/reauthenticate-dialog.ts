import { ChangeDetectionStrategy, Component, model, output, signal } from '@angular/core';
import { form, FormField, maxLength, required } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSpinner } from '@spartan-ng/helm/spinner';

//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { PASSWORD_MAX_LENGTH } from '../../validation/password-rule';

/**
 * 确认是本人：改邮箱、绑定外部登录、重置他人的口令或两步验证这类操作在会话不够新时，服务端要求出示本人的当前口令。
 * 只收口令并交给调用方重试原操作；口令对不对由服务端判定，失败按再认证计数。
 */
@Component({
  selector: 'app-reauthenticate-dialog',
  // prettier-ignore
  imports: [
    FormField,
    HlmButton,
    HlmInput,
    HlmSpinner,
    ...HlmDialogImports,
    ...HlmFieldImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './reauthenticate-dialog.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReauthenticateDialog {
  readonly visible = model(false);
  readonly saving = model(false);
  readonly confirmed = output<string>();
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  private readonly formModel = signal({ password: '' });
  // 只做必填与长度的快速反馈；口令对不对由服务端判定
  protected readonly reauthForm = form(this.formModel, (path) => {
    required(path.password);
    maxLength(path.password, PASSWORD_MAX_LENGTH);
  });

  /** 桥接 hlm-dialog 声明式 state 到对外 visible 契约；关闭时清掉口令。 */
  onDialogStateChange(state: BrnDialogState): void {
    const open = state === 'open';
    this.visible.set(open);
    if (!open) {
      this.formModel.set({ password: '' });
    }
  }

  onHide(): void {
    this.visible.set(false);
  }

  protected confirm(): void {
    if (this.reauthForm().invalid()) {
      this.reauthForm().markAsTouched();
      return;
    }
    if (!this.saving()) {
      this.confirmed.emit(this.formModel().password);
    }
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.reauthenticate.header': 'Confirm it is you',
  'common.reauthenticate.description':
    'This change affects how you or someone else signs in. Enter your current password to continue.',
  'common.reauthenticate.passwordLabel': 'Your current password',
  'common.reauthenticate.confirm': 'Confirm',
  'common.cancel': 'Cancel',
};
//#endif
