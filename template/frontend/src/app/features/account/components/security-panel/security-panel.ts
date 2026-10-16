import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { form, maxLength, minLength, required, validate, FormField } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideEye, lucideEyeOff, lucideLock } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import {
  HlmInputGroup,
  HlmInputGroupInput,
  HlmInputGroupButton,
} from '@spartan-ng/helm/input-group';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { finalize } from 'rxjs/operators';

import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
} from '../../../../core/validation/password-rule';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';
//#if (ExternalLogin)
import { ExternalLogins } from '../external-logins/external-logins';
//#endif
import { LoginDevices } from '../login-devices/login-devices';
import { TwoFactorSettings } from '../two-factor-settings/two-factor-settings';

/** 个人设置 ·「账户与安全」面板：修改密码、两步验证、已绑定的外部账号（启用外部登录时）、登录设备。 */
@Component({
  selector: 'app-security-panel',
  standalone: true,
  imports: [
    FormField,
    NgIcon,
    LoginDevices,
    TwoFactorSettings,
    //#if (ExternalLogin)
    ExternalLogins,
    //#endif
    HlmButton,
    HlmSpinner,
    HlmInputGroup,
    HlmInputGroupInput,
    HlmInputGroupButton,
    ...HlmFieldImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideEye, lucideEyeOff, lucideLock })],
  templateUrl: './security-panel.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SecurityPanel {
  private readonly accountService = inject(AccountService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  protected readonly loginDevices = viewChild(LoginDevices);

  readonly saving = signal(false);

  protected readonly showCurrentPassword = signal(false);

  protected readonly showNewPassword = signal(false);
  protected readonly showConfirmPassword = signal(false);

  private readonly formModel = signal({
    currentPassword: '',
    newPassword: '',
    confirmPassword: '',
  });

  readonly changeForm = form(this.formModel, (path) => {
    required(path.currentPassword);
    required(path.newPassword);
    minLength(path.newPassword, PASSWORD_MIN_LENGTH);
    maxLength(path.newPassword, PASSWORD_MAX_LENGTH);
    validate(path.newPassword, (ctx) => {
      const newPassword = ctx.value();
      const currentPassword = ctx.valueOf(path.currentPassword);
      return currentPassword && newPassword && currentPassword === newPassword
        ? { kind: 'passwordSameAsCurrent' }
        : null;
    });
    required(path.confirmPassword);
    validate(path.confirmPassword, (ctx) => {
      const confirm = ctx.value();
      const newPassword = ctx.valueOf(path.newPassword);
      return newPassword && confirm && newPassword !== confirm
        ? { kind: 'passwordMismatch' }
        : null;
    });
  });

  /** 清空表单并收起明文显示：面板不随改密成功销毁，旧密码与新密码不能留在页面上。 */
  private resetForm(): void {
    this.formModel.set({ currentPassword: '', newPassword: '', confirmPassword: '' });
    this.changeForm().reset();
    this.showCurrentPassword.set(false);
    this.showNewPassword.set(false);
    this.showConfirmPassword.set(false);
  }

  onSubmit(): void {
    if (this.changeForm().invalid()) {
      this.changeForm().markAsTouched();
      return;
    }

    this.saving.set(true);

    const { currentPassword, newPassword, confirmPassword } = this.formModel();

    this.accountService
      .changePassword({ currentPassword, newPassword, confirmPassword })
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: () => {
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('common.success'), {
            description: this.transloco.translate('account.changePassword.updateSuccess'),
          });
          //#else
          toast.success('Success', {
            description: 'Password updated. Other devices have been signed out.',
          });
          //#endif
          this.resetForm();
          // 服务端已让其他设备退出，列表跟着刷新
          this.loginDevices()?.reload();
        },
        error: (error) => {
          //#if (IncludeLocalization)
          toast.error(this.transloco.translate('common.requestError'), {
            description: applicationErrorMessage(error),
          });
          //#else
          toast.error('Request failed', { description: applicationErrorMessage(error) });
          //#endif
        },
      });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.changePassword.header': 'Change Password',
  'account.changePassword.subtitle':
    'Enter your current password and set a new sign-in password. Other devices will be signed out.',
  'account.changePassword.currentPassword': 'Current Password',
  'common.hidePassword': 'Hide password',
  'common.showPassword': 'Show password',
  'account.changePassword.newPassword': 'New Password',
  'account.changePassword.rules':
    'Password must be at least 12 characters (up to 256). A longer passphrase is stronger than a short complex one.',
  'account.changePassword.confirmPassword': 'Confirm New Password',
  'account.changePassword.submit': 'Update Password',
};
//#endif
