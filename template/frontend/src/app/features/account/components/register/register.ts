import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import {
  form,
  required,
  minLength,
  maxLength,
  email as emailValidator,
  pattern,
  FormField,
} from '@angular/forms/signals';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideEye, lucideEyeOff } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import {
  HlmInputGroup,
  HlmInputGroupInput,
  HlmInputGroupButton,
} from '@spartan-ng/helm/input-group';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { lastValueFrom } from 'rxjs';

import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { SecurityConfigService } from '../../../../core/services/security-config-service';
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
} from '../../../../core/validation/password-rule';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';
//#if (Email)
import { resendCountdown } from '../../utils/resend-countdown';
//#endif
import { CaptchaImage } from '../../widgets/captcha-image/captcha-image';
//#if (ExternalLogin)
import { ExternalLoginButtons } from '../../widgets/external-login-buttons/external-login-buttons';
//#endif
import { PasswordLengthMeter } from '../../widgets/password-length-meter/password-length-meter';
import { AuthShell } from '../auth-shell/auth-shell';

@Component({
  selector: 'app-register',
  standalone: true,
  imports: [
    FormField,
    RouterModule,
    NgIcon,
    HlmButton,
    HlmInput,
    HlmSpinner,
    HlmInputGroup,
    HlmInputGroupInput,
    HlmInputGroupButton,
    ...HlmFieldImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
    AuthShell,
    CaptchaImage,
    PasswordLengthMeter,
    //#if (ExternalLogin)
    ExternalLoginButtons,
    //#endif
  ],
  providers: [
    provideIcons({
      lucideEye,
      lucideEyeOff,
    }),
  ],
  templateUrl: './register.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Register implements OnInit {
  private accountService = inject(AccountService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  // returnUrl：注册成功后跳转 login 时传递
  returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');

  private _isLoading = signal(false);
  public readonly isLoading = this._isLoading.asReadonly();

  protected readonly showPassword = signal(false);

  /** 当前租户的注册摘要（登录入口选定的租户）；关闭自助注册时不给表单，只给说明。 */
  private readonly securityConfigService = inject(SecurityConfigService);
  protected readonly registrationOpen = this.securityConfigService.registrationOpen;
//#if (Email)
  protected readonly securityConfig = this.securityConfigService.config;
//#endif
  private readonly captcha = viewChild.required(CaptchaImage);

//#if (Email)
  readonly countdown = resendCountdown();
  private readonly emailVerificationChallenge = signal<{
    challengeId: string;
    email: string;
    expiresAt: number;
  } | null>(null);
  private _isSendingEmailCode = signal(false);
  public readonly isSendingEmailCode = this._isSendingEmailCode.asReadonly();

//#endif
  private readonly model = signal({
    email: '',
    username: '',
    captchaCode: '',
//#if (Email)
    emailVerificationCode: '',
//#endif
    password: '',
  });

  readonly registerForm = form(this.model, (path) => {
    required(path.email);
    emailValidator(path.email);
    maxLength(path.email, 256);
    required(path.username);
    // 长度与字符集共用一句提示，合成一条规则：分开校验会把同一句话报出几遍
    pattern(path.username, /^[a-zA-Z0-9_]{3,64}$/, { error: { kind: 'usernamePattern' } });
    required(path.captchaCode);
    maxLength(path.captchaCode, 10);
//#if (Email)
    required(path.emailVerificationCode, {
      when: () => this.securityConfig()?.enableEmailVerification === true,
    });
//#endif
    required(path.password);
    minLength(path.password, PASSWORD_MIN_LENGTH);
    maxLength(path.password, PASSWORD_MAX_LENGTH);
  });

  // 用户名是否被用户手动编辑过（一旦手动改动，停止从邮箱自动推导）
  private usernameManuallyEdited = false;
  // 最近一次自动推导写入的用户名，用于区分用户手动输入
  private lastDerivedUsername = '';

  constructor() {
    // 从邮箱前缀自动推导 username，用户手动改过 username 后停止推导。
    effect(() => {
      const email = this.model().email;
//#if (Email)
      const challenge = this.emailVerificationChallenge();
//#endif
      untracked(() => {
//#if (Email)
        if (challenge && challenge.email !== this.normalizeEmail(email)) {
          this.emailVerificationChallenge.set(null);
          this.model.update((m) => ({ ...m, emailVerificationCode: '' }));
          this.countdown.stop();
        }

//#endif
        const currentUsername = this.model().username;

        // 用户手动改动了 username（当前值既非空也不等于我们上次自动写入的值）
        if (currentUsername && currentUsername !== this.lastDerivedUsername) {
          this.usernameManuallyEdited = true;
        }

        if (this.usernameManuallyEdited || !email) {
          return;
        }

        const usernamePart = email.split('@')[0];
        if (usernamePart && usernamePart !== currentUsername) {
          this.lastDerivedUsername = usernamePart;
          this.model.update((m) => ({ ...m, username: usernamePart }));
        }
      });
    });
  }

  ngOnInit() {
    // 进入注册页时取当前策略：开关与验证方式可能在别的页面读过之后被管理员改过
    this.securityConfigService.refresh();
  }
  //#if (Email)

  async sendEmailCode() {
    const { email, captchaCode } = this.model();
    const captchaToken = this.captcha().token();

    if (!email || this.registerForm.email().invalid()) {
      //#if (IncludeLocalization)
      toast.warning(this.transloco.translate('common.notice'), {
        description: this.transloco.translate('account.register.emailRequired'),
      });
      //#else
      toast.warning('Notice', { description: 'Please enter a valid email first' });
      //#endif
      return;
    }

    if (!captchaCode || !captchaToken) {
      //#if (IncludeLocalization)
      toast.warning(this.transloco.translate('common.notice'), {
        description: this.transloco.translate('account.register.captchaRequired'),
      });
      //#else
      toast.warning('Notice', { description: 'Please enter the captcha first' });
      //#endif
      return;
    }

    this._isSendingEmailCode.set(true);
    try {
      const challenge = await lastValueFrom(
        this.accountService.sendEmailCode({
          email,
          captchaCode,
          captchaToken,
        }),
      );
      this.emailVerificationChallenge.set({
        challengeId: challenge.challengeId,
        email: this.normalizeEmail(email),
        expiresAt: Date.now() + challenge.expiresInSeconds * 1000,
      });
      //#if (IncludeLocalization)
      toast.success(this.transloco.translate('common.success'), {
        description: this.transloco.translate('account.register.emailCodeSent'),
      });
      //#else
      toast.success('Success', { description: 'Verification code sent, please check your email' });
      //#endif
      this.countdown.start(challenge.retryAfterSeconds);
    } catch (error) {
      this.showRequestError(error);
      void this.captcha().reload(); // 令牌只能用一次：失败后换一张
    } finally {
      this._isSendingEmailCode.set(false);
    }
  }
  //#endif

  async onSubmit() {
    if (this.registerForm().invalid()) {
      this.registerForm().markAsTouched();
      return;
    }

    const formValue = this.model();
    //#if (Email)

    const emailVerificationEnabled = this.securityConfig()?.enableEmailVerification === true;
    const challenge = this.emailVerificationChallenge();
    if (
      emailVerificationEnabled &&
      (!challenge ||
        challenge.email !== this.normalizeEmail(formValue.email) ||
        challenge.expiresAt <= Date.now())
    ) {
      this.registerForm.emailVerificationCode().markAsTouched();
      return;
    }
    //#endif
    this._isLoading.set(true);
    try {
      const captchaToken = this.captcha().token();

      if (!captchaToken) {
        //#if (IncludeLocalization)
        toast.error(this.transloco.translate('common.error'), {
          description: this.transloco.translate('account.register.captchaTokenMissing'),
        });
        //#else
        toast.error('Error', { description: 'Please refresh to get the captcha' });
        //#endif
        return;
      }

      await lastValueFrom(
        this.accountService.register({
          email: formValue.email,
          username: formValue.username,
          password: formValue.password,
          captchaCode: formValue.captchaCode,
          captchaToken: captchaToken,
//#if (Email)
          emailVerification: emailVerificationEnabled
            ? {
                challengeId: challenge!.challengeId,
                code: formValue.emailVerificationCode,
              }
            : undefined,
//#endif
        }),
      );

      //#if (IncludeLocalization)
      toast.success(this.transloco.translate('account.register.registerSuccess'), {
        description: this.transloco.translate('account.register.registerSuccessDetail'),
      });
      //#else
      toast.success('Account created', {
        description: 'Account created successfully, please sign in',
      });
      //#endif
      this.router.navigate(['/auth/login'], {
        queryParams: this.returnUrl ? { returnUrl: this.returnUrl } : undefined,
      });
    } catch (error) {
      this.showRequestError(error);
      void this.captcha().reload();
    } finally {
      this._isLoading.set(false);
    }
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
  //#if (Email)

  private normalizeEmail(email: string): string {
    return email.trim().toLowerCase();
  }
  //#endif
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.register.title': 'Create your account',
  'account.register.email': 'Email',
  'account.register.emailPlaceholder': 'Enter your email address',
  'account.register.username': 'Username',
  'account.register.usernamePlaceholder':
    'Extracted from email prefix, letters, digits and underscores allowed',
  'account.captcha.label': 'Captcha',
  'account.captcha.placeholder': 'Enter the captcha on the right',
  'account.register.emailCode': 'Email Verification Code',
  'account.register.emailCodePlaceholder': 'Enter the 6-digit code',
  'account.register.resendCountdown': 'Resend in {{seconds}}s',
  'account.register.sendCode': 'Get Code',
  'account.register.password': 'Password',
  'account.register.passwordPlaceholder': 'At least 12 characters',
  'common.hidePassword': 'Hide password',
  'common.showPassword': 'Show password',
  'account.register.submit': 'Sign Up',
  'account.register.haveAccount': 'Already have an account?',
  'account.register.loginNow': 'Sign in',
  'account.register.closed': 'Self sign-up is not open here. Ask an administrator for an account.',
  //#if (ExternalLogin)
  'account.register.or': 'Or sign up with',
  //#endif
};
//#endif
