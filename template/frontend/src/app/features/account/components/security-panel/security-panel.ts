import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { form, maxLength, minLength, required, validate, FormField } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideEye,
  lucideEyeOff,
  //#if (ExternalLogin)
  lucideKeyRound,
  //#endif
  lucideLock,
} from '@ng-icons/lucide';
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

import { PasswordLengthMeter } from '../../../../core/components/password-length-meter/password-length-meter';
//#if (ExternalLogin)
import {
  ReauthenticateDialog,
  ReauthenticationProof,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import { injectReauthentication } from '../../../../core/components/reauthenticate-dialog/reauthentication';
//#endif
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { AuthService } from '../../../../core/services/auth-service';
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
//#if (Email)
import { TwoFactorEmailSettings } from '../two-factor-email-settings/two-factor-email-settings';
//#endif
import { TwoFactorPasskeySettings } from '../two-factor-passkey-settings/two-factor-passkey-settings';
import { TwoFactorSettings } from '../two-factor-settings/two-factor-settings';

/**
 * 个人设置 ·「账户与安全」面板：修改密码、通行密钥、两步验证、已绑定的外部账号（启用外部登录时）、登录设备。
 * 只经外部登录建立、还没有口令的账号把"修改密码"换成"设置密码"。
 */
@Component({
  selector: 'app-security-panel',
  standalone: true,
  imports: [
    FormField,
    NgIcon,
    LoginDevices,
    TwoFactorPasskeySettings,
    TwoFactorSettings,
    //#if (Email)
    TwoFactorEmailSettings,
    //#endif
    //#if (ExternalLogin)
    ExternalLogins,
    ReauthenticateDialog,
    //#endif
    HlmButton,
    HlmSpinner,
    PasswordLengthMeter,
    HlmInputGroup,
    HlmInputGroupInput,
    HlmInputGroupButton,
    ...HlmFieldImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  // prettier-ignore
  providers: [
    provideIcons({
      lucideEye,
      lucideEyeOff,
      //#if (ExternalLogin)
      lucideKeyRound,
      //#endif
      lucideLock,
    }),
  ],
  templateUrl: './security-panel.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SecurityPanel {
  private readonly accountService = inject(AccountService);
  private readonly authService = inject(AuthService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  protected readonly loginDevices = viewChild(LoginDevices);
  protected readonly passkeys = viewChild(TwoFactorPasskeySettings);
  protected readonly twoFactor = viewChild(TwoFactorSettings);
  //#if (Email)
  protected readonly emailTwoFactor = viewChild(TwoFactorEmailSettings);
  //#endif
  //#if (ExternalLogin)
  protected readonly externalLogins = viewChild(ExternalLogins);
  //#endif

  /**
   * 账号是否设有本地口令，取自当前用户。只经外部登录建立的账号没有，此时"修改密码"换成"设置密码"；
   * 没有外部登录的形态下所有账号都有口令。
   */
  protected readonly hasPassword = computed(
    () => this.authService.currentUser()?.hasPassword ?? true,
  );

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

  //#if (ExternalLogin)
  private readonly setModel = signal({ newPassword: '', confirmPassword: '' });

  readonly setForm = form(this.setModel, (path) => {
    required(path.newPassword);
    minLength(path.newPassword, PASSWORD_MIN_LENGTH);
    maxLength(path.newPassword, PASSWORD_MAX_LENGTH);
    required(path.confirmPassword);
    validate(path.confirmPassword, (ctx) => {
      const confirm = ctx.value();
      const newPassword = ctx.valueOf(path.newPassword);
      return newPassword && confirm && newPassword !== confirm
        ? { kind: 'passwordMismatch' }
        : null;
    });
  });

  /** 设置口令而会话不够新：确认身份（账号没有口令，只能出示验证码或重新登录）后重试。 */
  protected readonly reauth = injectReauthentication();

  /**
   * 设置第一个口令。先不带凭证提交，服务端要求确认身份时才弹出对话框；刚完成外部登录的会话可以直接设置。
   * 成功后刷新当前用户（区块换回"修改密码"）和外部绑定列表（有了口令，最后一个绑定也能解绑了）。
   */
  onSetPasswordSubmit(proof?: ReauthenticationProof): void {
    if (this.setForm().invalid()) {
      this.setForm().markAsTouched();
      return;
    }

    this.saving.set(true);
    const { newPassword, confirmPassword } = this.setModel();
    this.accountService
      .setPassword({
        newPassword,
        confirmPassword,
        twoFactorCode: proof?.twoFactorCode,
        passkey: proof?.passkey,
      })
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: () => {
          this.reauth.complete();
          this.setModel.set({ newPassword: '', confirmPassword: '' });
          this.setForm().reset();
          this.showNewPassword.set(false);
          this.showConfirmPassword.set(false);
          this.authService.loadUser().subscribe({ error: () => undefined });
          this.externalLogins()?.reload();
          // 与改口令一致，服务端已让其他设备退出
          this.loginDevices()?.reload();
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('common.success'), {
            description: this.transloco.translate('account.setPassword.success'),
          });
          //#else
          toast.success('Success', {
            description: 'Password set. You can now also sign in with your email and password.',
          });
          //#endif
        },
        error: (error) => {
          if (this.reauth.handleError(error, proof, (next) => this.onSetPasswordSubmit(next))) {
            return;
          }
          this.showRequestError(error);
        },
      });
  }

  //#endif
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
        error: (error) => this.showRequestError(error),
      });
  }

  private showRequestError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error('Request failed', { description: applicationErrorMessage(error) });
    //#endif
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.changePassword.header': 'Change password',
  'account.changePassword.subtitle':
    'Enter your current password and set a new sign-in password. Other devices will be signed out.',
  'account.changePassword.currentPassword': 'Current password',
  'common.hidePassword': 'Hide password',
  'common.showPassword': 'Show password',
  'account.changePassword.newPassword': 'New password',
  'account.changePassword.confirmPassword': 'Confirm new password',
  'account.changePassword.submit': 'Update password',
  //#if (ExternalLogin)
  'account.setPassword.header': 'Set a password',
  'account.setPassword.subtitle':
    'You can currently sign in only with a third-party account. After setting a password, you can also sign in with your username or email and password.',
  'account.setPassword.submit': 'Set password',
  'account.setPassword.reauthDescription': 'Confirm it is you before setting a password.',
  //#endif
};
//#endif
