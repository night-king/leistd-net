import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  OnInit,
  output,
  signal,
} from '@angular/core';
import { form, FormField, maxLength, required } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheck, lucideCopy } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { toDataURL } from 'qrcode';
import { finalize } from 'rxjs/operators';

import { API_ERROR_CODES } from '../../../../core/errors/api-error-codes';
import {
  ApplicationHttpError,
  applicationErrorMessage,
} from '../../../../core/errors/application-http-error';
import { PASSWORD_MAX_LENGTH } from '../../../../core/validation/password-rule';
import { injectCopyToClipboard } from '../../../../shared/utils/clipboard';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { BeginTwoFactorSetupInputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';
import { OtpCodeInput } from '../otp-code-input/otp-code-input';

/**
 * 设置两步验证：添加密钥到身份验证器后输入验证码确认。个人设置、换绑与"组织要求启用"的强制页共用；
 * 确认后发出恢复码，二维码在浏览器里生成，密钥不经过第三方服务。
 *
 * 服务端要求"刚认证过"：会话不够新时先在这里出示当前口令；没有本地口令的账号只能重新登录。
 * 这一次设置失效（过期、别处重新开始、凭据变过）时清掉二维码、密钥与已输入的码，请本人重新开始。
 */
@Component({
  selector: 'app-two-factor-setup',
  // prettier-ignore
  imports: [
    FormField,
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
  providers: [provideIcons({ lucideCheck, lucideCopy })],
  templateUrl: './two-factor-setup.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TwoFactorSetup implements OnInit {
  private readonly accountService = inject(AccountService);
  private readonly clipboard = injectCopyToClipboard();
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 确认后发出恢复码（明文只有这一次）。 */
  readonly enabled = output<string[]>();
  /** 是否提供"取消"：组织要求启用时没有退路，不给这个按钮。 */
  readonly cancellable = input(true);
  readonly cancelled = output<void>();
  /** 换绑：现有验证器上的码或一个恢复码。首次启用时为 null。 */
  readonly proof = input<Pick<BeginTwoFactorSetupInputDto, 'code' | 'recoveryCode'> | null>(null);
  /** 换绑出示的码或恢复码不对：交回调用方重新输入。 */
  readonly proofRejected = output<void>();

  /**
   * 设置所处的阶段：加载中、要出示口令、要重新登录、可扫码确认、已失效、启动失败。
   * 已失效时二维码与密钥已清掉，只能重新开始。
   */
  protected readonly stage = signal<
    'loading' | 'password' | 'signInAgain' | 'ready' | 'expired' | 'failed'
  >('loading');
  protected readonly secret = signal<string | null>(null);
  protected readonly qrDataUrl = signal<string | null>(null);
  protected readonly code = signal('');
  private readonly passwordModel = signal({ password: '' });
  // 只做必填与长度的快速反馈；口令对不对由服务端判定
  protected readonly passwordForm = form(this.passwordModel, (path) => {
    required(path.password);
    maxLength(path.password, PASSWORD_MAX_LENGTH);
  });
  protected readonly submitting = signal(false);
  protected readonly copied = this.clipboard.copied;

  private setupId: string | null = null;
  // 二维码异步生成：重新开始或失效之后，上一轮晚到的结果不能再画上去
  private generation = 0;

  /** 按 4 位一组显示密钥，照抄时不容易看串行。 */
  protected readonly groupedSecret = computed(
    () =>
      this.secret()
        ?.match(/.{1,4}/g)
        ?.join(' ') ?? '',
  );
  protected readonly canSubmit = computed(
    () => /^\d{6}$/.test(this.code().replace(/\s/g, '')) && !this.submitting(),
  );
  protected readonly replacing = computed(() => this.proof() !== null);

  // 生成待启用密钥是写请求，不放构造函数：组件实例化（含测试装配）不应改变服务端状态。
  ngOnInit(): void {
    this.start();
  }

  /** 开始（或重新开始）一次设置；会话不够新时带上刚输入的口令。 */
  protected start(password?: string): void {
    const generation = ++this.generation;
    this.clearSetup();
    this.stage.set('loading');
    this.accountService.beginTwoFactorSetup({ ...this.proof(), password }).subscribe({
      next: async (setup) => {
        if (generation !== this.generation) {
          return;
        }
        this.setupId = setup.setupId;
        this.secret.set(setup.secret);
        this.passwordModel.set({ password: '' });
        this.stage.set('ready');
        try {
          const qr = await this.renderQrCode(setup.otpAuthUri);
          if (generation === this.generation) {
            this.qrDataUrl.set(qr);
          }
        } catch {
          // 二维码生成失败时仍可手动输入密钥
        }
      },
      error: (error: unknown) => {
        if (generation !== this.generation) {
          return;
        }
        switch (errorCode(error)) {
          case API_ERROR_CODES.reauthenticationRequired:
            this.stage.set('password');
            return;
          case API_ERROR_CODES.signInAgainRequired:
            this.stage.set('signInAgain');
            return;
          case API_ERROR_CODES.twoFactorCodeInvalid:
            // 只有换绑出示的码或恢复码会被这样拒绝：交回调用方重新输入
            this.showError(error);
            this.proofRejected.emit();
            return;
          default:
            this.showError(error);
            // 口令错了留在口令这一步重输；其他失败给"重试"
            this.stage.set(password !== undefined ? 'password' : 'failed');
        }
      },
    });
  }

  protected submitPassword(): void {
    if (this.passwordForm().invalid()) {
      this.passwordForm().markAsTouched();
      return;
    }
    this.start(this.passwordModel().password);
  }

  // 剪贴板不可用（非安全上下文等）时用户仍能手动选中复制
  protected copySecret(): void {
    const secret = this.secret();
    if (secret) {
      void this.clipboard.copy(secret);
    }
  }

  protected submit(): void {
    const setupId = this.setupId;
    if (!this.canSubmit() || setupId === null) {
      return;
    }

    this.submitting.set(true);
    this.accountService
      .enableTwoFactor(setupId, this.code().replace(/\s/g, ''))
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({
        next: (result) => this.enabled.emit(result.recoveryCodes),
        error: (error: unknown) => {
          if (errorCode(error) === API_ERROR_CODES.twoFactorSetupExpired) {
            // 这一份密钥再也确认不了：不留在页面上，请本人重新开始
            this.generation++;
            this.clearSetup();
            this.stage.set('expired');
            return;
          }
          this.showError(error);
        },
      });
  }

  /** 在浏览器里生成二维码，密钥不经过第三方服务。 */
  protected renderQrCode(otpAuthUri: string): Promise<string> {
    return toDataURL(otpAuthUri, { margin: 1, width: 192 });
  }

  private clearSetup(): void {
    this.setupId = null;
    this.secret.set(null);
    this.qrDataUrl.set(null);
    this.code.set('');
  }

  private showError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error(this.t('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#endif
  }
}

function errorCode(error: unknown): string | undefined {
  return error instanceof ApplicationHttpError ? error.code : undefined;
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.requestError': 'Request error',
  'common.retry': 'Retry',
  'common.cancel': 'Cancel',
  'account.twoFactor.scanTitle': '1. Add this account to your authenticator app',
  'account.twoFactor.scanHint':
    'Scan the QR code with an app such as Google Authenticator, Microsoft Authenticator or Tencent Authenticator.',
  'account.twoFactor.manualHint': "Can't scan it? Enter this key instead:",
  'account.twoFactor.copyKey': 'Copy key',
  'account.twoFactor.copied': 'Copied',
  'account.twoFactor.qrAlt': 'QR code for the authenticator app',
  'account.twoFactor.verifyTitle': '2. Enter the 6-digit code shown in the app',
  'account.twoFactor.codeLabel': 'Verification code',
  'account.twoFactor.enable': 'Verify and turn on',
  'account.twoFactor.setupLoadFailed': "Couldn't start the setup.",
  'account.twoFactor.replaceConfirm': 'Verify and replace',
  'account.twoFactor.reauthHint':
    'To protect your account, enter your current password to continue.',
  'account.twoFactor.passwordLabel': 'Current password',
  'account.twoFactor.continue': 'Continue',
  'account.twoFactor.signInAgainHint':
    'To protect your account, sign out and sign in again, then set it up right away.',
  'account.twoFactor.setupExpired':
    'This setup is no longer valid (it expired or was restarted elsewhere). The key above was discarded.',
  'account.twoFactor.restart': 'Start again',
};
//#endif
