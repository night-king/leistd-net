import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideFingerprint,
  lucideKeyRound,
  lucideMail,
  lucideShieldCheck,
  lucideSmartphone,
} from '@ng-icons/lucide';
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
import { getPasskey, passkeySupport } from '../../../../core/webauthn/webauthn';
import {
  classifyWebAuthnError,
  isWebAuthnError,
  WebAuthnFailure,
} from '../../../../core/webauthn/webauthn-error';
import { OtpCodeInput } from '../../../../shared/components/otp-code-input/otp-code-input';
import { SessionLoginOutputDto, TwoFactorMethod } from '../../../../shared/dtos/auth.dto';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { TwoFactorLoginInputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';
//#if (Email)
import { resendCountdown } from '../../utils/resend-countdown';
//#endif

/** 登录第二步：第一步交来的凭据、本人此刻能用的方式与默认方式。 */
export interface TwoFactorPrompt {
  token: string;
  /** 能用的方式，按系统顺序，恢复码在最后。 */
  methods: TwoFactorMethod[];
  /** 直接给出的方式。 */
  defaultMethod: TwoFactorMethod;
  /** 邮箱验证码能用时，收码地址的打码形式。 */
  maskedEmail: string | null;
}

/** 从第一步（密码或外部登录）的结果里取出第二步；不需要第二步时为 null。 */
export function twoFactorPromptOf(
  result: SessionLoginOutputDto | null | undefined,
): TwoFactorPrompt | null {
  if (!result?.requiresTwoFactor || !result.twoFactorToken) {
    return null;
  }

  const methods: TwoFactorMethod[] = result.twoFactorMethods?.length
    ? result.twoFactorMethods
    : ['authenticator'];
  return {
    token: result.twoFactorToken,
    methods,
    defaultMethod: result.defaultTwoFactorMethod ?? methods[0],
    maskedEmail: result.maskedEmail ?? null,
  };
}

const INPUT_IDS: Record<TwoFactorMethod, string> = {
  passkey: 'two-factor-passkey',
  authenticator: 'two-factor-code',
  email: 'two-factor-email-code',
  recoveryCode: 'two-factor-recovery-code',
};
const ERROR_ID = 'two-factor-error';

/** 这次第二步已经无法继续的错误：遇到它们回到密码那一步，其余就地说明、可以重试。 */
const SECOND_STEP_ENDED: ReadonlySet<string> = new Set([
  API_ERROR_CODES.twoFactorChallengeExpired,
  API_ERROR_CODES.userLockedOut,
  API_ERROR_CODES.userTemporarilyLockedOut,
  API_ERROR_CODES.userDisabled,
]);
const REUSE_HINT_ID = 'two-factor-code-reuse-hint';

/** 各方式在"换一种验证方式"列表里的图标。 */
const METHOD_ICONS: Record<TwoFactorMethod, string> = {
  passkey: 'lucideFingerprint',
  authenticator: 'lucideSmartphone',
  email: 'lucideMail',
  recoveryCode: 'lucideKeyRound',
};

/**
 * 登录第二步：直接给出默认方式（本人首选，否则按 通行密钥 → 身份验证器 → 邮箱验证码 的顺序），不先让本人选；
 * 通过后由登录页建立会话上下文，凭据过期或次数用完时回到密码那一步。
 *
 * 换方式的入口在下方：只有一种备选时直接写明（如"改用恢复码"），两种以上时折叠成"换一种验证方式"，
 * 展开后列出其余方式，恢复码在最后。邮箱验证码在用到时才发：默认就是它，或本人切换过来。
 *
 * 通行密钥只给一个按钮，本人点了才调起浏览器（不自动弹出：浏览器要求用户手势，也不该一进来就弹系统对话框）；
 * 每次点击重新取一份断言选项。没通过、本人取消或这次仪式已失效时就地说明，可以再点一次或换一种方式。
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
  providers: [
    provideIcons({
      lucideFingerprint,
      lucideKeyRound,
      lucideMail,
      lucideShieldCheck,
      lucideSmartphone,
    }),
  ],
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

  /** 第一步交来的第二步。 */
  readonly prompt = input.required<TwoFactorPrompt>();
  /** 第二步通过，会话已下发。 */
  readonly completed = output<void>();
  /** 回到密码那一步（用户选择返回，或凭据已失效）。 */
  readonly cancelled = output<void>();

  /** 当前用的方式；换了一次第二步时回到它的默认方式。 */
  protected readonly method = linkedSignal(() => this.prompt().defaultMethod);
  /** 其余能换的方式，按系统顺序。 */
  protected readonly alternatives = computed(() =>
    this.prompt().methods.filter((method) => method !== this.method()),
  );
  /** 两种以上备选时，"换一种验证方式"列表是否展开。 */
  protected readonly showAlternatives = signal(false);
  protected readonly value = signal('');
  protected readonly submitting = signal(false);
  /** 码被拒或发不出码的原因（服务端已本地化），显示在输入框下方。 */
  protected readonly error = signal<string | null>(null);
  /** 被拒的是码本身（不是方式不能用）。 */
  protected readonly codeRejected = signal(false);
  /** 最近提交过的值：输满自动提交与回车同时到达时只交一次，改动输入后才允许再交。 */
  private submittedValue: string | null = null;
  //#if (Email)
  /** 这一次第二步是否已经发过邮箱验证码。 */
  protected readonly emailSent = signal(false);
  protected readonly sendingEmail = signal(false);
  protected readonly resend = resendCountdown();
  //#endif

  /** 通行密钥在浏览器这一侧没成（本人取消或认证器失败）：按归类给出说明，不显示浏览器原文。 */
  protected readonly passkeyFailure = signal<WebAuthnFailure | null>(null);
  /** 浏览器此刻能不能用通行密钥（不支持或不在安全上下文时按钮不可用，并说明原因）。 */
  protected readonly passkeySupport = passkeySupport();

  protected readonly inputIds = INPUT_IDS;
  protected readonly methodIcons = METHOD_ICONS;
  protected readonly errorId = ERROR_ID;
  protected readonly reuseHintId = REUSE_HINT_ID;

  /** 身份验证器的码被拒：同一个码只能用一次，提示刚用过的要等新码（邮箱验证码与恢复码不会这样）。 */
  protected readonly showReuseHint = computed(
    () => this.codeRejected() && this.method() === 'authenticator',
  );
  protected readonly codeDescribedBy = computed(() => {
    if (!this.error()) {
      return null;
    }
    return this.showReuseHint() ? `${ERROR_ID} ${REUSE_HINT_ID}` : ERROR_ID;
  });

  protected readonly canSubmit = computed(() => {
    const value = this.value().replace(/\s/g, '');
    if (this.submitting() || this.method() === 'passkey') {
      return false;
    }

    return this.method() === 'recoveryCode' ? value.length >= 8 : /^\d{6}$/.test(value);
  });
  //#if (Email)

  constructor() {
    // 默认就是邮箱验证码：进来即发，不用本人再点一次（首次渲染之后输入已就位）
    afterNextRender(() => {
      if (this.method() === 'email') {
        void this.sendEmailCode();
      }
    });
  }
  //#endif

  /** 展开其余方式：按钮随之换成列表，焦点落到列表第一项，读屏与键盘不会落到页面顶部。 */
  protected expandAlternatives(): void {
    this.showAlternatives.set(true);
    afterNextRender(
      () =>
        this.document
          .querySelector<HTMLElement>('[data-testid="two-factor-methods"] button')
          ?.focus(),
      { injector: this.injector },
    );
  }

  /** 换到另一种方式：清掉输入与说明，收起列表，焦点落到新方式的输入框。 */
  protected selectMethod(method: TwoFactorMethod): void {
    this.method.set(method);
    this.showAlternatives.set(false);
    this.setValue('');
    this.error.set(null);
    this.codeRejected.set(false);
    this.passkeyFailure.set(null);
    //#if (Email)
    if (method === 'email' && !this.emailSent()) {
      void this.sendEmailCode();
    }
    //#endif
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
      this.codeRejected.set(false);
    }
  }
  //#if (Email)

  /** 把验证码发到本人的邮箱；发码间隔内重发由倒计时挡住，服务端也会拒绝。 */
  protected async sendEmailCode(): Promise<void> {
    if (this.sendingEmail() || this.resend.remaining() > 0) {
      return;
    }

    this.sendingEmail.set(true);
    this.error.set(null);
    try {
      const result = await lastValueFrom(
        this.accountService.sendTwoFactorEmailCode(this.prompt().token),
      );
      this.emailSent.set(true);
      this.resend.start(result.retryAfterSeconds);
    } catch (error) {
      // 只有这次第二步已经无法继续（凭据过期、账号锁定或停用）才回到密码那一步；
      // 发信失败、发得太频繁、方式此刻不能用都不作废挑战：就地说明，本人可以稍后重发或换一种方式
      if (error instanceof ApplicationHttpError && SECOND_STEP_ENDED.has(error.code ?? '')) {
        this.leave(error);
        return;
      }
      this.error.set(applicationErrorMessage(error));
    } finally {
      this.sendingEmail.set(false);
    }
  }
  //#endif

  protected async submit(): Promise<void> {
    if (!this.canSubmit() || this.value() === this.submittedValue) {
      return;
    }

    this.submitting.set(true);
    this.submittedValue = this.value();
    try {
      await lastValueFrom(this.accountService.completeTwoFactorLogin(this.loginInput()));
      this.completed.emit();
    } catch (error) {
      const code = error instanceof ApplicationHttpError ? error.code : undefined;
      // 只有这次第二步已经无法继续（凭据过期、账号锁定或停用）才回到密码那一步
      if (code !== undefined && SECOND_STEP_ENDED.has(code)) {
        this.leave(error);
        return;
      }

      this.error.set(applicationErrorMessage(error));
      if (code === API_ERROR_CODES.twoFactorCodeInvalid) {
        // 码不对：清空重输
        this.codeRejected.set(true);
        this.setValue('');
        this.focusInput();
        return;
      }

      // 方式此刻不能用、邮箱码已失效、网络错误或服务端故障：挑战仍有效，就地说明，同一个码也可以再提交一次
      this.submittedValue = null;
      this.focusInput();
    } finally {
      this.submitting.set(false);
    }
  }

  /** 用通行密钥完成第二步：本人点击后取断言选项、调起浏览器，再交回断言。 */
  protected async verifyWithPasskey(): Promise<void> {
    if (this.submitting() || this.passkeySupport !== 'supported') {
      return;
    }

    this.submitting.set(true);
    this.error.set(null);
    this.passkeyFailure.set(null);
    const token = this.prompt().token;
    try {
      const { options } = await lastValueFrom(this.accountService.beginTwoFactorPasskey(token));
      const passkey = await getPasskey(options);
      await lastValueFrom(this.accountService.completeTwoFactorLogin({ token, passkey }));
      this.completed.emit();
    } catch (error) {
      const code = error instanceof ApplicationHttpError ? error.code : undefined;
      if (code !== undefined && SECOND_STEP_ENDED.has(code)) {
        this.leave(error);
        return;
      }
      if (isWebAuthnError(error)) {
        this.passkeyFailure.set(classifyWebAuthnError(error));
      } else {
        // 没通过、这次仪式已失效（再点一次即换一份选项）、方式此刻不能用、网络问题：挑战仍有效，就地说明
        this.error.set(applicationErrorMessage(error));
      }
      this.focusInput();
    } finally {
      this.submitting.set(false);
    }
  }

  private loginInput(): TwoFactorLoginInputDto {
    const token = this.prompt().token;
    const value = this.value().trim();
    switch (this.method()) {
      case 'recoveryCode':
        return { token, recoveryCode: value };
      //#if (Email)
      case 'email':
        return { token, emailCode: value.replace(/\s/g, '') };
      //#endif
      default:
        return { token, code: value.replace(/\s/g, '') };
    }
  }

  // 凭据过期、次数用完或账号被锁：这一步已经无法继续，回到密码那一步，原因随 toast 带过去
  private leave(error: unknown): void {
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
    const id = INPUT_IDS[this.method()];
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
  'account.twoFactorLogin.useRecovery': 'Use a recovery code',
  'account.twoFactorLogin.useCode': 'Use authenticator app',
  'account.twoFactorLogin.usePasskey': 'Use a passkey',
  'account.twoFactorLogin.passkeyHint': 'Use your passkey to finish signing in.',
  'account.twoFactorLogin.passkeySubmit': 'Use passkey',
  'account.twoFactorLogin.passkeyCancelled':
    'The passkey request was cancelled. Try again when ready.',
  'account.twoFactorLogin.passkeyFailed':
    "The passkey couldn't be used. Try again, or try another way.",
  'account.twoFactorLogin.passkeyUnsupported':
    "This browser can't use passkeys here. Try another way.",
  'account.twoFactorLogin.otherMethods': 'Try another way',
  'account.twoFactorLogin.otherMethodsLabel': 'Other verification methods',
  'account.twoFactorLogin.methods.passkey.name': 'Passkey',
  'account.twoFactorLogin.methods.passkey.hint': 'Use your fingerprint, face or screen lock',
  'account.twoFactorLogin.methods.authenticator.name': 'Authenticator app',
  'account.twoFactorLogin.methods.authenticator.hint': 'Enter the 6-digit code from the app',
  'account.twoFactorLogin.methods.recoveryCode.name': 'Recovery code',
  'account.twoFactorLogin.methods.recoveryCode.hint': 'Use one of the codes you saved',
  // 输入框标签的三元式在两种形态下都在：不随邮件能力裁剪
  'account.twoFactorLogin.emailLabel': 'Email verification code',
  //#if (Email)
  'account.twoFactorLogin.emailHint': 'We sent a 6-digit code to {{email}}.',
  'account.twoFactorLogin.emailPending': 'A code will be sent to {{email}}.',
  'account.twoFactorLogin.emailSending': 'Sending a code to {{email}}…',
  'account.twoFactorLogin.resend': 'Resend code',
  'account.twoFactorLogin.resendIn': 'Resend in {{seconds}}s',
  'account.twoFactorLogin.useEmail': 'Use an email code',
  'account.twoFactorLogin.methods.email.name': 'Email verification code',
  'account.twoFactorLogin.methods.email.hint': 'Send a code to {{email}}',
  //#endif
  'account.twoFactorLogin.back': 'Back to sign in',
  'common.twoFactorCodeReuseHint': 'If you just used this code, wait for a new one.',
};
//#endif
