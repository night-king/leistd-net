import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideArrowLeft, lucideShieldCheck } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { lastValueFrom } from 'rxjs';

import { API_ERROR_CODES } from '../../../../core/errors/api-error-codes';
import {
  ApplicationHttpError,
  applicationErrorMessage,
} from '../../../../core/errors/application-http-error';
import { OtpCodeInput } from '../../../../shared/components/otp-code-input/otp-code-input';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';

const CODE_INPUT_ID = 'two-factor-code';
const RECOVERY_INPUT_ID = 'two-factor-recovery-code';
const ERROR_ID = 'two-factor-error';

/**
 * 登录第二步：输入身份验证器验证码或恢复码；通过后由登录页建立会话上下文，凭据过期或次数用完时
 * 回到密码那一步。
 *
 * 码不对时就地说明（服务端给出的原因，例如同一个码刚用过、要等下一个），清空输入并把焦点放回去；
 * 说明留到本人开始重输。输满 6 位自动提交，同一个码只交一次。
 */
@Component({
  selector: 'app-two-factor-challenge',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    ...HlmFieldImports,
    HlmInput,
    HlmSpinner,
    OtpCodeInput,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideArrowLeft, lucideShieldCheck })],
  templateUrl: './two-factor-challenge.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TwoFactorChallenge {
  private readonly accountService = inject(AccountService);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly codeInput = viewChild(OtpCodeInput);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 第一步返回的凭据。 */
  readonly token = input.required<string>();
  /** 第二步通过，会话已下发。 */
  readonly completed = output<void>();
  /** 回到密码那一步（用户选择返回，或凭据已失效）。 */
  readonly cancelled = output<void>();

  protected readonly useRecoveryCode = signal(false);
  protected readonly value = signal('');
  protected readonly submitting = signal(false);
  /** 码被拒的原因（服务端已本地化），显示在输入框下方。 */
  protected readonly error = signal<string | null>(null);
  /** 最近提交过的值：输满自动提交与回车同时到达时只交一次，改动输入后才允许再交。 */
  private submittedValue: string | null = null;

  protected readonly codeInputId = CODE_INPUT_ID;
  protected readonly recoveryInputId = RECOVERY_INPUT_ID;
  protected readonly errorId = ERROR_ID;

  protected readonly canSubmit = computed(() => {
    const value = this.value().replace(/\s/g, '');
    return (
      !this.submitting() && (this.useRecoveryCode() ? value.length >= 8 : /^\d{6}$/.test(value))
    );
  });

  protected toggleMode(): void {
    this.useRecoveryCode.update((v) => !v);
    this.setValue('');
    this.error.set(null);
    this.focusInput();
  }

  protected onInput(event: Event): void {
    this.onValueChange((event.target as HTMLInputElement).value);
  }

  protected onValueChange(value: string): void {
    this.setValue(value);
    // 清空不算改动：被拒后由本组件清空，说明要留到本人开始重输
    if (value.length > 0) {
      this.error.set(null);
    }
  }

  protected async submit(): Promise<void> {
    if (!this.canSubmit() || this.value() === this.submittedValue) {
      return;
    }

    this.submitting.set(true);
    this.submittedValue = this.value();
    const value = this.value().trim();
    try {
      await lastValueFrom(
        this.accountService.completeTwoFactorLogin(
          this.useRecoveryCode()
            ? { token: this.token(), recoveryCode: value }
            : { token: this.token(), code: value.replace(/\s/g, '') },
        ),
      );
      this.completed.emit();
    } catch (error) {
      const code = error instanceof ApplicationHttpError ? error.code : undefined;
      if (code === API_ERROR_CODES.twoFactorCodeInvalid) {
        this.error.set(applicationErrorMessage(error));
        this.setValue('');
        this.focusInput();
        return;
      }
      // 凭据过期、次数用完或账号被锁：这一步已经无法继续，回到密码那一步，原因随 toast 带过去
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('account.login.loginFailed'), {
        description: applicationErrorMessage(error),
      });
      //#else
      toast.error(this.t('account.login.loginFailed'), {
        description: applicationErrorMessage(error),
      });
      //#endif
      this.cancelled.emit();
    } finally {
      this.submitting.set(false);
    }
  }

  private setValue(value: string): void {
    this.value.set(value);
    // 被拒可能与本次输入发生在同一轮变更检测之内：绑定看不出值变过，直接清空输入组件本身
    if (value === '') {
      this.codeInput()?.value.set('');
    }
    this.submittedValue = null;
  }

  // 切换方式或输错清空后，焦点回到当前的真实输入框
  private focusInput(): void {
    const id = this.useRecoveryCode() ? RECOVERY_INPUT_ID : CODE_INPUT_ID;
    afterNextRender(() => this.document.getElementById(id)?.focus(), {
      injector: this.injector,
    });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.login.loginFailed': 'Sign-in failed',
  'account.twoFactorLogin.title': 'Two-factor authentication',
  'account.twoFactorLogin.codeHint': 'Enter the 6-digit code from your authenticator app.',
  'account.twoFactorLogin.recoveryHint': 'Enter one of the recovery codes you saved.',
  'account.twoFactorLogin.codeLabel': 'Verification code',
  'account.twoFactorLogin.recoveryLabel': 'Recovery code',
  'account.twoFactorLogin.submit': 'Verify',
  'account.twoFactorLogin.useRecovery': "Can't use your authenticator? Use a recovery code",
  'account.twoFactorLogin.useCode': 'Use an authenticator code instead',
  'account.twoFactorLogin.back': 'Back to sign in',
};
//#endif
