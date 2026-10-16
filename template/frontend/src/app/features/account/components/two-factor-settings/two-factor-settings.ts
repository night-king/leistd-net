import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideShieldCheck } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { finalize } from 'rxjs/operators';

import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { AuthService } from '../../../../core/services/auth-service';
import { StatusDot } from '../../../../shared/directives/status-dot';
import { TwoFactorStatusOutputDto } from '../../../../shared/dtos/account-security.dto';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';
import { OtpCodeInput } from '../otp-code-input/otp-code-input';
import { RecoveryCodes } from '../recovery-codes/recovery-codes';
import { TwoFactorSetup } from '../two-factor-setup/two-factor-setup';

type Mode = 'idle' | 'setup' | 'codes' | 'disable' | 'regenerate' | 'replace' | 'replaceSetup';

/**
 * 「账户与安全」面板的一节：两步验证的启用、换绑、停用与恢复码。启用、换绑与停用都会让本人其他设备退出，
 * 面板据 {@link sessionsChanged} 刷新设备列表。
 *
 * 换绑先出示现有验证器上的码；验证器丢了时用一个恢复码（当场用掉），再走与首次启用相同的设置。
 * 不经过"停用"，组织强制两步验证时同样可用。
 */
@Component({
  selector: 'app-two-factor-settings',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmInput,
    HlmSpinner,
    OtpCodeInput,
    RecoveryCodes,
    StatusDot,
    TwoFactorSetup,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideShieldCheck })],
  templateUrl: './two-factor-settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TwoFactorSettings {
  private readonly accountService = inject(AccountService);
  private readonly authService = inject(AuthService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 本人的其他会话被服务端撤销了（启用、停用两步验证时）。 */
  readonly sessionsChanged = output<void>();

  protected readonly status = signal<TwoFactorStatusOutputDto | null>(null);
  protected readonly loadFailed = signal(false);
  protected readonly mode = signal<Mode>('idle');
  protected readonly codes = signal<string[]>([]);
  protected readonly busy = signal(false);

  protected readonly password = signal('');
  protected readonly code = signal('');
  protected readonly recoveryCode = signal('');
  /** 换绑时用恢复码代替验证码（验证器已经丢了）。 */
  protected readonly useRecoveryCode = signal(false);
  /** 换绑出示的第二因素，交给设置组件随开始设置一并提交。 */
  protected readonly proof = signal<{ code?: string; recoveryCode?: string } | null>(null);
  protected readonly codeValid = computed(() => /^\d{6}$/.test(this.code().replace(/\s/g, '')));

  constructor() {
    this.reload();
  }

  protected reload(): void {
    this.loadFailed.set(false);
    this.accountService.getTwoFactorStatus().subscribe({
      next: (status) => this.status.set(status),
      error: () => this.loadFailed.set(true),
    });
  }

  protected setMode(mode: Mode): void {
    this.password.set('');
    this.code.set('');
    this.recoveryCode.set('');
    if (mode !== 'replaceSetup') {
      this.proof.set(null);
    }
    this.mode.set(mode);
  }

  protected toggleRecoveryCode(): void {
    this.useRecoveryCode.update((value) => !value);
    this.code.set('');
    this.recoveryCode.set('');
  }

  protected onRecoveryCodeInput(event: Event): void {
    this.recoveryCode.set((event.target as HTMLInputElement).value);
  }

  protected canReplace(): boolean {
    return this.useRecoveryCode() ? this.recoveryCode().trim().length > 0 : this.codeValid();
  }

  protected startReplace(): void {
    if (!this.canReplace()) {
      return;
    }

    this.proof.set(
      this.useRecoveryCode()
        ? { recoveryCode: this.recoveryCode().trim() }
        : { code: this.code().replace(/\s/g, '') },
    );
    this.setMode('replaceSetup');
  }

  protected onPasswordInput(event: Event): void {
    this.password.set((event.target as HTMLInputElement).value);
  }

  protected onEnabled(codes: string[]): void {
    const replaced = this.mode() === 'replaceSetup';
    this.proof.set(null);
    this.codes.set(codes);
    this.mode.set('codes');
    this.reload();
    this.refreshCurrentUser();
    this.sessionsChanged.emit();
    const message = replaced ? 'account.twoFactor.replacedToast' : 'account.twoFactor.enabledToast';
    //#if (IncludeLocalization)
    toast.success(this.transloco.translate(message));
    //#else
    toast.success(this.t(message));
    //#endif
  }

  protected finishCodes(): void {
    this.codes.set([]);
    this.setMode('idle');
  }

  protected disable(): void {
    if (!this.password() || !this.codeValid() || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.accountService
      .disableTwoFactor({ password: this.password(), code: this.code().replace(/\s/g, '') })
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: () => {
          this.setMode('idle');
          this.reload();
          this.refreshCurrentUser();
          this.sessionsChanged.emit();
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('account.twoFactor.disabledToast'));
          //#else
          toast.success(this.t('account.twoFactor.disabledToast'));
          //#endif
        },
        error: (error) => this.showError(error),
      });
  }

  protected regenerate(): void {
    if (!this.codeValid() || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.accountService
      .regenerateRecoveryCodes(this.code().replace(/\s/g, ''))
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: (result) => {
          this.codes.set(result.recoveryCodes);
          this.mode.set('codes');
          this.reload();
        },
        error: (error) => this.showError(error),
      });
  }

  // 顶栏等处读的是当前用户上的 isTwoFactorEnabled，跟着刷新
  private refreshCurrentUser(): void {
    this.authService.loadUser().subscribe({ error: () => undefined });
  }

  private showError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error(this.t('common.requestError'), { description: applicationErrorMessage(error) });
    //#endif
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.requestError': 'Request error',
  'common.retry': 'Retry',
  'common.cancel': 'Cancel',
  'account.twoFactor.header': 'Two-factor authentication',
  'account.twoFactor.subtitleOff':
    'Besides your password, sign-in also asks for a code from an authenticator app on your phone.',
  'account.twoFactor.subtitleOn':
    'Sign-in asks for a code from your authenticator app after your password.',
  'account.twoFactor.on': 'On',
  'account.twoFactor.turnOn': 'Turn on',
  'account.twoFactor.turnOff': 'Turn off',
  'account.twoFactor.regenerate': 'Regenerate recovery codes',
  'account.twoFactor.recoveryCodesLeft': '{{count}} unused recovery code(s) left',
  'account.twoFactor.requiredByPolicy':
    'Your organization requires two-factor authentication, so it cannot be turned off.',
  'account.twoFactor.disableHint':
    'Enter your password and a code from the authenticator app to turn it off.',
  'account.twoFactor.regenerateHint':
    'Enter a code from the authenticator app. The old recovery codes stop working.',
  'account.twoFactor.passwordLabel': 'Current password',
  'account.twoFactor.codeLabel': 'Verification code',
  'account.twoFactor.confirmDisable': 'Turn off',
  'account.twoFactor.confirmRegenerate': 'Regenerate',
  'account.twoFactor.enabledToast':
    'Two-factor authentication is on. Other devices were signed out.',
  'account.twoFactor.disabledToast':
    'Two-factor authentication is off. Other devices were signed out.',
  'account.twoFactor.loadFailed': "Couldn't load the two-factor status.",
  'account.twoFactor.replace': 'Replace authenticator',
  'account.twoFactor.replaceHint':
    'Enter a code from your current authenticator app. Lost it? Use a recovery code instead; it is used up right away.',
  'account.twoFactor.useRecoveryCode': 'Lost your authenticator? Use a recovery code',
  'account.twoFactor.useCode': 'Use a verification code instead',
  'account.twoFactor.recoveryCodeLabel': 'Recovery code',
  'account.twoFactor.continue': 'Continue',
  'account.twoFactor.replacedToast':
    'Authenticator replaced. The old one and its recovery codes no longer work, and other devices were signed out.',
};
//#endif
