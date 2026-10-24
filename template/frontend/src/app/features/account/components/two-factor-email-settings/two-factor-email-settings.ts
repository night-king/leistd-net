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
import { lucideMail } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { finalize } from 'rxjs/operators';

import {
  ReauthenticateDialog,
  ReauthenticationProof,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import { injectReauthentication } from '../../../../core/components/reauthenticate-dialog/reauthentication';
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
import { OtpCodeInput } from '../../../../shared/components/otp-code-input/otp-code-input';
import { StatusDot } from '../../../../shared/directives/status-dot';
import { TwoFactorStatusOutputDto } from '../../../../shared/dtos/account-security.dto';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';
import { resendCountdown } from '../../utils/resend-countdown';
import { canChooseDefault } from '../../utils/two-factor-methods';

const CODE_INPUT_ID = 'two-factor-email-code';

/**
 * 「账户与安全」面板的一节：邮箱验证码作为登录第二步的开关；两种方式都能用时，标出默认的那一种或给"设为默认"。
 *
 * 邮箱验证码只作补充：组织要求两步验证时它不算满足，组织也可以不允许它。开启先确认是本人，
 * 再输入发到当前邮箱的确认码；开启、关闭都会让本人其他设备退出，面板据 {@link sessionsChanged} 刷新设备列表。
 * 用不了时（组织未允许、邮箱未验证、部署发不了码）写明原因，不给开启按钮。
 */
@Component({
  selector: 'app-two-factor-email-settings',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmBadge,
    HlmButton,
    ...HlmFieldImports,
    HlmSpinner,
    OtpCodeInput,
    ReauthenticateDialog,
    StatusDot,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideMail })],
  templateUrl: './two-factor-email-settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TwoFactorEmailSettings {
  private readonly accountService = inject(AccountService);
  private readonly confirmService = inject(ConfirmService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 本人的其他会话被服务端撤销了（开启、关闭邮箱验证码时）。 */
  readonly sessionsChanged = output<void>();
  /** 登录默认方式变了（这一节里"设为默认"）：面板据此刷新身份验证器一节。 */
  readonly defaultChanged = output<void>();

  protected readonly reauth = injectReauthentication();
  protected readonly resend = resendCountdown();
  protected readonly status = signal<TwoFactorStatusOutputDto | null>(null);
  protected readonly loadFailed = signal(false);
  protected readonly busy = signal(false);
  /** 开启中：确认码已发出，等本人输入的挑战 Id。 */
  protected readonly challengeId = signal<string | null>(null);
  protected readonly code = signal('');
  protected readonly codeInputId = CODE_INPUT_ID;
  protected readonly codeValid = computed(() => /^\d{6}$/.test(this.code().trim()));
  /** 邮箱验证码能用、还有别的方式可选（通行密钥、身份验证器）：才有"默认"可言。 */
  protected readonly defaultChoice = computed(() => canChooseDefault(this.status(), 'email'));

  constructor() {
    this.reload();
  }

  /** 重新读取状态；身份验证器那一节启用、停用后由面板调用。 */
  reload(): void {
    this.loadFailed.set(false);
    this.accountService.getTwoFactorStatus().subscribe({
      next: (status) => this.status.set(status),
      error: () => this.loadFailed.set(true),
    });
  }

  /** 开始开启：确认是本人后发确认码。 */
  protected begin(proof?: ReauthenticationProof): void {
    this.busy.set(true);
    this.accountService
      .beginEmailTwoFactorSetup({
        password: proof?.password,
        twoFactorCode: proof?.twoFactorCode,
        passkey: proof?.passkey,
      })
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: (challenge) => {
          this.reauth.complete();
          this.code.set('');
          this.challengeId.set(challenge.challengeId);
          this.resend.start(challenge.retryAfterSeconds);
        },
        error: (error) => {
          if (!this.reauth.handleError(error, proof, (next) => this.begin(next))) {
            this.showError(error);
          }
        },
      });
  }

  protected confirm(): void {
    const challengeId = this.challengeId();
    if (!challengeId || !this.codeValid() || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.accountService
      .enableEmailTwoFactor({ challengeId, code: this.code().trim() })
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: () => {
          this.cancel();
          this.reload();
          this.sessionsChanged.emit();
          this.showSuccess('account.twoFactorEmail.enabledToast');
        },
        error: (error) => this.showError(error),
      });
  }

  protected cancel(): void {
    this.challengeId.set(null);
    this.code.set('');
    this.resend.stop();
  }

  protected onReauthenticationVisibleChange(visible: boolean): void {
    const saving = this.reauth.saving();
    this.reauth.setVisible(visible);
    if (!visible && !saving) this.busy.set(false);
  }
  protected async turnOff(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    let submitted = false;
    try {
      const confirmed = await this.confirmService.open({
        //#if (IncludeLocalization)
        message: this.transloco.translate('account.twoFactorEmail.turnOffConfirm'),
        confirmText: this.transloco.translate('account.twoFactorEmail.turnOff'),
        //#else
        message: this.t('account.twoFactorEmail.turnOffConfirm'),
        confirmText: this.t('account.twoFactorEmail.turnOff'),
        //#endif
        variant: 'destructive',
      });
      if (confirmed) {
        this.disable();
        submitted = true;
      }
    } finally {
      if (!submitted) this.busy.set(false);
    }
  }

  /** 登录时默认先给邮箱验证码；不增减方式，不要求确认身份。 */
  protected setAsDefault(): void {
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.accountService
      .setPreferredTwoFactorMethod('email')
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: () => {
          this.reload();
          this.defaultChanged.emit();
          this.showSuccess('account.twoFactor.defaultToast');
        },
        error: (error) => this.showError(error),
      });
  }

  private disable(proof?: ReauthenticationProof): void {
    this.busy.set(true);
    this.accountService
      .disableEmailTwoFactor({
        password: proof?.password,
        twoFactorCode: proof?.twoFactorCode,
        passkey: proof?.passkey,
      })
      .pipe(
        finalize(() => {
          if (!this.reauth.visible()) this.busy.set(false);
        }),
      )
      .subscribe({
        next: () => {
          this.reauth.complete();
          this.reload();
          this.sessionsChanged.emit();
          this.showSuccess('account.twoFactorEmail.disabledToast');
        },
        error: (error) => {
          if (!this.reauth.handleError(error, proof, (next) => this.disable(next))) {
            this.showError(error);
          }
        },
      });
  }

  private showSuccess(key: string): void {
    //#if (IncludeLocalization)
    toast.success(this.transloco.translate(key));
    //#else
    toast.success(this.t(key));
    //#endif
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
  'account.twoFactorEmail.header': 'Email verification codes',
  'account.twoFactorEmail.subtitle':
    'Sign-in can also use a code sent to {{email}}. It adds to your authenticator but does not count as two-factor authentication where your organization requires it.',
  'account.twoFactorEmail.on': 'On',
  'account.twoFactorEmail.turnOn': 'Turn on',
  'account.twoFactorEmail.turnOff': 'Turn off',
  'account.twoFactorEmail.turnOffConfirm':
    'Sign-in will no longer offer codes sent to your email. Other devices will be signed out.',
  'account.twoFactorEmail.loadFailed': "Couldn't load the email verification status.",
  'account.twoFactorEmail.confirmHint':
    'We sent a confirmation code to {{email}}. Enter it to turn on email verification codes.',
  'account.twoFactorEmail.codeLabel': 'Confirmation code',
  'account.twoFactorEmail.confirm': 'Turn on',
  'account.twoFactorEmail.resend': 'Resend code',
  'account.twoFactorEmail.resendIn': 'Resend in {{seconds}}s',
  'account.twoFactorEmail.unavailable.notAllowed':
    "Your organization doesn't allow email codes for signing in.",
  'account.twoFactorEmail.unavailable.emailNotVerified':
    'Verify your email address in your profile first.',
  'account.twoFactorEmail.unavailable.deliveryUnavailable':
    "Codes can't be sent right now. Contact your administrator.",
  'account.twoFactorEmail.unusedWhileUnavailable':
    'It is on, but sign-in will not use it until then.',
  'account.twoFactorEmail.enabledToast':
    'Email verification codes are on. Other devices were signed out.',
  'account.twoFactorEmail.disabledToast':
    'Email verification codes are off. Other devices were signed out.',
  'account.twoFactor.default': 'Default',
  'account.twoFactor.setDefault': 'Set as default',
  'account.twoFactor.defaultToast': 'Saved. Sign-in will ask for this method first.',
  'account.twoFactorEmail.reauthDescription': 'Confirm it is you before changing how you sign in.',
};
//#endif
