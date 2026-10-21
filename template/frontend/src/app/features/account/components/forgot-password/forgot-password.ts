// prettier-ignore
import {
  ChangeDetectionStrategy, Component, inject,
  //#if (Email)
  computed, signal, viewChild,
  //#endif
} from '@angular/core';
//#if (Email)
import { form, required, minLength, maxLength, email, FormField } from '@angular/forms/signals';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
//#else
import { ActivatedRoute, RouterModule } from '@angular/router';
//#endif
//#if (IncludeLocalization)
// prettier-ignore
import {
  TranslocoDirective,
  //#if (Email)
  TranslocoService,
  //#endif
} from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideArrowLeft,
  //#if (Email && IncludeMultiTenancy)
  lucideBuilding2,
  //#endif
  //#if (Email)
  lucideEye,
  lucideEyeOff,
  //#endif
} from '@ng-icons/lucide';
//#if (Email)
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
//#endif

//#if (Email)
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { SecurityConfigService } from '../../../../core/services/security-config-service';
//#if (IncludeMultiTenancy)
import { TenantContextService } from '../../../../core/services/tenant-context-service';
//#endif
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
} from '../../../../core/validation/password-rule';
//#endif
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
//#if (Email)
import { AccountService } from '../../services/account-service';
//#if (IncludeMultiTenancy)
import { hostTenantProbe } from '../../utils/host-tenant-probe';
//#endif
import { resendCountdown } from '../../utils/resend-countdown';
import { CaptchaImage } from '../../widgets/captcha-image/captcha-image';
import { PasswordLengthMeter } from '../../widgets/password-length-meter/password-length-meter';
//#endif
import { AuthShell } from '../auth-shell/auth-shell';

/**
 * 忘记密码：凭已验证邮箱上的验证码设置新口令。两步在同一页：先过图形验证码发码，再填码与新口令。
 * 不论邮箱是否对应账号，发码的响应都一样，页面也不据此区分；不含邮件或部署缺验证码前提时只给说明。
 */
@Component({
  selector: 'app-forgot-password',
  standalone: true,
  // prettier-ignore
  imports: [
    RouterModule,
    NgIcon,
    //#if (Email)
    FormField,
    HlmButton,
    HlmInput,
    HlmSpinner,
    HlmInputGroup,
    HlmInputGroupInput,
    HlmInputGroupButton,
    ...HlmFieldImports,
    CaptchaImage,
    PasswordLengthMeter,
    //#endif
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
    AuthShell,
  ],
  // prettier-ignore
  providers: [
    provideIcons({
      lucideArrowLeft,
      //#if (Email && IncludeMultiTenancy)
      lucideBuilding2,
      //#endif
      //#if (Email)
      lucideEye,
      lucideEyeOff,
      //#endif
    }),
  ],
  templateUrl: './forgot-password.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ForgotPassword {
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  /** 登录完成后要接续的地址：原样转交给登录页，是否可接续由登录页判定。 */
  protected readonly returnUrl = inject(ActivatedRoute).snapshot.queryParamMap.get('returnUrl');
  //#if (Email)
  private readonly accountService = inject(AccountService);
  private readonly router = inject(Router);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#endif
  //#if (IncludeMultiTenancy)
  protected readonly tenantContext = inject(TenantContextService);
  /** 与登录页同一套域名定案：直接打开或刷新本页时，本地记住的租户可能与服务端按域名采用的不同。 */
  protected readonly tenantProbe = hostTenantProbe();
  /** 租户上下文未定案时不发码：挑战按租户隔离，落错租户的码在第二步用不了。 */
  protected readonly tenantBlocked = this.tenantProbe.blocked;
  //#else
  protected readonly tenantBlocked = () => false;
  //#endif
  private readonly securityConfig = inject(SecurityConfigService);

  /** 部署具备发验证码的前提；读到之前按可用处理，免得表单先闪一下说明。 */
  protected readonly available = computed(
    () => this.securityConfig.config()?.emailVerificationAvailable ?? true,
  );

  readonly isLoading = signal(false);
  protected readonly showPassword = signal(false);
  private readonly captcha = viewChild(CaptchaImage);
  readonly countdown = resendCountdown();

  /** 已发出的挑战；有值即第二步。 */
  protected readonly challenge = signal<{ challengeId: string; email: string } | null>(null);

  private readonly sendModel = signal({
    // 登录页填的是邮箱时经导航状态带来
    email:
      (this.router.currentNavigation()?.extras.state as { email?: string } | undefined)?.email ??
      '',
    captchaCode: '',
  });

  readonly sendForm = form(this.sendModel, (path) => {
    required(path.email);
    email(path.email);
    maxLength(path.email, 256);
    required(path.captchaCode);
    maxLength(path.captchaCode, 10);
  });

  private readonly resetModel = signal({ code: '', newPassword: '' });

  readonly resetForm = form(this.resetModel, (path) => {
    required(path.code);
    maxLength(path.code, 10);
    required(path.newPassword);
    minLength(path.newPassword, PASSWORD_MIN_LENGTH);
    maxLength(path.newPassword, PASSWORD_MAX_LENGTH);
  });

  constructor() {
    this.securityConfig.refresh();
  }

  async sendCode(): Promise<void> {
    const captchaToken = this.captcha()?.token();
    if (this.tenantBlocked()) {
      return;
    }
    if (this.sendForm().invalid() || !captchaToken) {
      this.sendForm().markAsTouched();
      return;
    }

    this.isLoading.set(true);
    try {
      const { email: address, captchaCode } = this.sendModel();
      const result = await lastValueFrom(
        this.accountService.sendPasswordResetCode({
          email: address.trim(),
          captchaToken,
          captchaCode,
        }),
      );
      this.challenge.set({ challengeId: result.challengeId, email: address.trim() });
      this.resetModel.set({ code: '', newPassword: '' });
      this.countdown.start(result.retryAfterSeconds);
    } catch (error) {
      this.showRequestError(error);
      void this.captcha()?.reload(); // 令牌只能用一次：失败后换一张
    } finally {
      this.isLoading.set(false);
    }
  }

  /** 回到发码一步：换邮箱或重发都要重新过图形验证码（回到这一步时验证码图片重新创建，自带一张新图）。 */
  backToSend(): void {
    this.challenge.set(null);
  }

  async reset(): Promise<void> {
    const challenge = this.challenge();
    if (!challenge || this.resetForm().invalid()) {
      this.resetForm().markAsTouched();
      return;
    }

    this.isLoading.set(true);
    try {
      const { code, newPassword } = this.resetModel();
      await lastValueFrom(
        this.accountService.resetPassword({
          email: challenge.email,
          challengeId: challenge.challengeId,
          code: code.trim(),
          newPassword,
        }),
      );
      // 不自动登录：开着两步验证的账号仍要走第二步，统一回登录页
      //#if (IncludeLocalization)
      toast.success(this.transloco.translate('account.forgotPassword.done'), {
        description: this.transloco.translate('account.forgotPassword.doneDetail'),
      });
      //#else
      toast.success('Password reset', { description: 'Sign in with your new password.' });
      //#endif
      await this.router.navigate(['/auth/login'], {
        queryParams: this.returnUrl ? { returnUrl: this.returnUrl } : undefined,
      });
    } catch (error) {
      this.showRequestError(error);
    } finally {
      this.isLoading.set(false);
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
  //#endif
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.forgotPassword.title': 'Reset your password',
  //#if (Email)
  'account.forgotPassword.description':
    "Enter your account's email and we'll send you a verification code.",
  'account.forgotPassword.sent':
    'If {{email}} is a verified email of an account, a verification code is on its way.',
  'account.forgotPassword.email': 'Email',
  'account.forgotPassword.emailPlaceholder': 'Enter the email of your account',
  'account.forgotPassword.sendCode': 'Send code',
  'account.forgotPassword.code': 'Verification code',
  'account.forgotPassword.notReceived': "Didn't get it?",
  'account.forgotPassword.resend': 'Send again',
  'account.forgotPassword.newPassword': 'New password',
  'account.forgotPassword.submit': 'Reset password',
  'account.captcha.label': 'Captcha',
  'account.captcha.placeholder': 'Enter the captcha on the right',
  'account.forgotPassword.codePlaceholder': 'Enter the 6-digit code',
  'account.forgotPassword.resendCountdown': 'Resend in {{seconds}}s',
  'account.forgotPassword.newPasswordPlaceholder': 'At least 12 characters',
  'common.hidePassword': 'Hide password',
  'common.showPassword': 'Show password',
  //#if (IncludeMultiTenancy)
  'account.login.tenant': 'Tenant',
  'account.login.tenantHost': 'Host',
  'account.login.tenantResolving': 'Identifying tenant from the site address…',
  'account.login.tenantProbeRetry': 'Retry',
  'account.login.tenantUnavailable':
    'The tenant this address points to is unavailable. Contact your administrator.',
  'account.login.tenantProbeFailed':
    'Could not determine the tenant for this address. Check your connection and try again.',
  //#endif
  //#endif
  'account.forgotPassword.unavailable': 'Ask an administrator to reset your password.',
  'account.forgotPassword.backToLogin': 'Back to sign in',
};
//#endif
