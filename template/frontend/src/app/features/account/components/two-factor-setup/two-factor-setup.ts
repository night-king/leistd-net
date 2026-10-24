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
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheck, lucideCopy } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { toDataURL } from 'qrcode';
import { finalize } from 'rxjs/operators';

import {
  ReauthenticateDialog,
  ReauthenticationProof,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import { injectReauthentication } from '../../../../core/components/reauthenticate-dialog/reauthentication';
import { API_ERROR_CODES } from '../../../../core/errors/api-error-codes';
import {
  ApplicationHttpError,
  applicationErrorMessage,
} from '../../../../core/errors/application-http-error';
import { OtpCodeInput } from '../../../../shared/components/otp-code-input/otp-code-input';
import { injectCopyToClipboard } from '../../../../shared/utils/clipboard';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { BeginTwoFactorSetupInputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';

/**
 * 设置两步验证：添加密钥到身份验证器后输入验证码确认。个人设置、换绑与"组织要求启用"的强制页共用；
 * 确认后发出恢复码，二维码在浏览器里生成，密钥不经过第三方服务。
 *
 * 服务端要求"刚认证过"：先不带凭证开始，会话不够新时弹出确认身份的对话框，带上本人交出的凭证重新开始。
 * 换绑时现有验证器上的码同时算确认身份，服务端只校验一次，不再另弹对话框；用恢复码换绑时它不算确认身份，
 * 会话不够新仍要确认。这一次设置失效（过期、别处重新开始、凭据变过）时清掉二维码、密钥与已输入的码，请本人重新开始。
 */
@Component({
  selector: 'app-two-factor-setup',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    ...HlmFieldImports,
    HlmSpinner,
    OtpCodeInput,
    ReauthenticateDialog,
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
   * 设置所处的阶段：加载中、等本人确认身份、可扫码确认、已失效、启动失败。
   * 已失效时二维码与密钥已清掉，只能重新开始。
   */
  protected readonly stage = signal<'loading' | 'reauth' | 'ready' | 'expired' | 'failed'>(
    'loading',
  );
  protected readonly secret = signal<string | null>(null);
  protected readonly qrDataUrl = signal<string | null>(null);
  protected readonly code = signal('');
  protected readonly submitting = signal(false);
  /** 会话不够新：确认身份后带上凭证重新开始。 */
  protected readonly reauth = injectReauthentication();
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

  /** 开始（或重新开始）一次设置；`reauthProof` 是确认身份的对话框交出的凭证。 */
  protected start(reauthProof?: ReauthenticationProof): void {
    const generation = ++this.generation;
    this.clearSetup();
    // 带着凭证重试时对话框还开着，背后保持"等确认身份"，不闪成加载中
    if (reauthProof === undefined) {
      this.stage.set('loading');
    }
    this.accountService.beginTwoFactorSetup(this.beginInput(reauthProof)).subscribe({
      next: async (setup) => {
        if (generation !== this.generation) {
          return;
        }
        this.reauth.complete();
        this.setupId = setup.setupId;
        this.secret.set(setup.secret);
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
        // 要求确认身份，或对话框里交出的凭证被拒：留在"等确认身份"，原因在对话框里就地说明
        if (this.reauth.handleError(error, reauthProof, (next) => this.start(next))) {
          this.stage.set('reauth');
          return;
        }
        const proofRejected =
          errorCode(error) === API_ERROR_CODES.twoFactorCodeInvalid && reauthProof === undefined;
        // 换绑表单里出示的是验证码时，它可能刚在登录时用过：同一个码只能用一次，提示等新码
        this.showError(error, proofRejected && this.proof()?.code !== undefined);
        if (proofRejected) {
          // 换绑表单里出示的码或恢复码不对：交回调用方重新输入
          this.proofRejected.emit();
          return;
        }
        this.stage.set('failed');
      },
    });
  }

  /**
   * 开始设置的请求体。对话框交出验证码（换绑而会话不够新、验证器还在）时，它同时证明持有现有因素，
   * 服务端只校验一次，不再另带恢复码或口令；交出口令时与换绑表单里的恢复码一起提交。
   */
  private beginInput(reauthProof?: ReauthenticationProof): BeginTwoFactorSetupInputDto {
    if (reauthProof?.twoFactorCode !== undefined) {
      return { code: reauthProof.twoFactorCode };
    }
    return { ...this.proof(), password: reauthProof?.password, passkey: reauthProof?.passkey };
  }

  /** 本人关掉了确认身份的对话框：个人设置里视同取消；强制设置页没有退路，留下重新打开的入口。 */
  protected onReauthVisibleChange(visible: boolean): void {
    this.reauth.setVisible(visible);
    if (!visible && this.stage() === 'reauth' && this.cancellable()) {
      this.cancelled.emit();
    }
  }

  /** 强制设置页上重新打开确认身份：不带凭证重新开始，由服务端决定是否仍需确认。 */
  protected retryReauthentication(): void {
    this.start();
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

  /** `codeRejected`：被拒的是本人输入的验证码，补一句刚用过的码要等新码。 */
  private showError(error: unknown, codeRejected = false): void {
    const message = applicationErrorMessage(error);
    //#if (IncludeLocalization)
    const hint = codeRejected ? this.transloco.translate('common.twoFactorCodeReuseHint') : '';
    toast.error(this.transloco.translate('common.requestError'), {
      description: hint ? `${message} ${hint}` : message,
    });
    //#else
    const hint = codeRejected ? this.t('common.twoFactorCodeReuseHint') : '';
    toast.error(this.t('common.requestError'), {
      description: hint ? `${message} ${hint}` : message,
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
  'common.twoFactorCodeReuseHint': 'If you just used this code, wait for a new one.',
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
  'account.twoFactor.reauthPending':
    'Confirm it is you before setting up two-factor authentication.',
  'account.twoFactor.reauthOpen': 'Confirm it is you',
  'account.twoFactor.reauthDescription':
    'Setting up an authenticator changes how you sign in. Confirm it is you to continue.',
  'account.twoFactor.setupExpired':
    'This setup is no longer valid (it expired or was restarted elsewhere). The key above was discarded.',
  'account.twoFactor.restart': 'Start again',
};
//#endif
