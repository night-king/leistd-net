import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  OnInit,
  signal,
  untracked,
} from '@angular/core';
import { form, minLength, maxLength, required, FormField } from '@angular/forms/signals';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import { lucideBuilding2, lucideCircleAlert, lucideFingerprint, lucideInfo, lucideEye, lucideEyeOff } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
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

import { API_ERROR_CODES } from '../../../../core/errors/api-error-codes';
import {
  ApplicationHttpError,
  applicationErrorMessage,
} from '../../../../core/errors/application-http-error';
import { MOCKED_URL } from '../../../../core/mock/mocked-url';
import { ReturnUrlService } from '../../../../core/routing/return-url-service';
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { SecurityConfigService } from '../../../../core/services/security-config-service';
import { SessionContextService } from '../../../../core/services/session-context-service';
//#if (IncludeMultiTenancy)
import { TenantContextService } from '../../../../core/services/tenant-context-service';
//#endif
import { PASSWORD_MAX_LENGTH } from '../../../../core/validation/password-rule';
import {
  getPasskey,
  isConditionalMediationAvailable,
  passkeySupport,
} from '../../../../core/webauthn/webauthn';
import {
  classifyWebAuthnError,
  isWebAuthnError,
  WebAuthnFailure,
} from '../../../../core/webauthn/webauthn-error';
import { WebAuthnJson } from '../../../../shared/dtos/account-security.dto';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';
//#if (IncludeMultiTenancy)
import { hostTenantProbe } from '../../utils/host-tenant-probe';
//#endif
//#if (ExternalLogin)
import { ExternalLoginButtons } from '../../widgets/external-login-buttons/external-login-buttons';
//#endif
import { AuthShell } from '../auth-shell/auth-shell';
import {
  TwoFactorChallenge,
  TwoFactorPrompt,
  twoFactorPromptOf,
} from '../two-factor-challenge/two-factor-challenge';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [
    FormField,
    RouterModule,
    NgIcon,
    ...HlmAlertImports,
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
    TwoFactorChallenge,
    //#if (ExternalLogin)
    ExternalLoginButtons,
    //#endif
  ],
  // prettier-ignore
  providers: [
    provideIcons({
      lucideBuilding2,
      lucideCircleAlert,
      lucideFingerprint,
      lucideInfo,
      lucideEye,
      lucideEyeOff,
    }),
  ],
  templateUrl: './login.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Login implements OnInit {
  private authService = inject(AuthService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly sessionContext = inject(SessionContextService);
  /** 租户关闭自助注册时不给注册入口。 */
  private readonly securityConfig = inject(SecurityConfigService);
  protected readonly registrationOpen = this.securityConfig.registrationOpen;
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private readonly returnUrls = inject(ReturnUrlService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif
  //#if (IncludeMultiTenancy)
  protected readonly tenantContext = inject(TenantContextService);
  //#endif

  private _isLoading = signal(false);
  public readonly isLoading = this._isLoading.asReadonly();

  protected readonly showPassword = signal(false);

  /**
   * 凭据被拒（用户名或密码不对、账号已锁定等）时服务端给出的原因，就地显示在"登录"按钮上方；
   * 改动任一输入即清掉。连不上服务器这类全局问题仍用 toast。
   */
  protected readonly loginError = signal<string | null>(null);

  // 登录接口由 Mock 应答时才提示演示账号：只 Mock 了别的模块时，演示账号登不进真实后端
  public readonly isMockEnabled = signal(inject(MOCKED_URL)(AuthService.loginUrl));
  /**
   * 登录完成后回到的地址（OIDC 授权回跳等）。外部登录与去找回页都原样带上它，找回后回到登录页仍能接续；
   * 是否可接续只在登录完成时由 ReturnUrlService 判定。
   */
  protected readonly returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');

  private readonly model = signal({
    usernameOrEmail: '',
    password: '',
  });

  readonly loginForm = form(this.model, (path) => {
    required(path.usernameOrEmail);
    minLength(path.usernameOrEmail, 3);
    maxLength(path.usernameOrEmail, 256);
    required(path.password);
    // 登录只设防滥用上限，不校验口令策略：策略生效前设置的旧口令也必须能登录。
    maxLength(path.password, PASSWORD_MAX_LENGTH);
  });

  /**
   * 带去找回页的预填：填的是邮箱就带上，省得再输一遍。走导航状态而不是查询串：邮箱不进地址栏，
   * 也不随地址进 Referer（导航状态本身会写入 history.state）。
   */
  protected readonly forgotPasswordState = computed(() => {
    const value = this.model().usernameOrEmail.trim();
    return value.includes('@') ? { email: value } : {};
  });

  /**
   * 第二步：密码已通过，尚待其中一种方式通过。有值时登录页换成第二步。
   *
   * 外部登录回调遇到需要第二步的账号时，经导航状态把它带过来（不放进地址栏）。
   */
  private readonly callbackReturnUrl = (
    this.router.currentNavigation()?.extras.state as { returnUrl?: string } | undefined
  )?.returnUrl;

  protected readonly twoFactorPrompt = signal<TwoFactorPrompt | null>(
    (
      this.router.currentNavigation()?.extras.state as
        { twoFactorPrompt?: TwoFactorPrompt } | undefined
    )?.twoFactorPrompt ?? null,
  );

  private readonly accountService = inject(AccountService);
  private readonly destroyRef = inject(DestroyRef);

  /** 这一次收到 `Auth:PasskeyUnavailable`（部署此刻没配）：收起通行密钥的入口。 */
  private readonly passkeyWithdrawn = signal(false);
  /**
   * 给出"用通行密钥登录"与用户名框里的条件式自动填充：部署配了依赖方（注册摘要的 `passkeysAvailable`），
   * 且浏览器支持、在安全上下文里。取回摘要前不给，免得给了又收。
   */
  protected readonly showPasskey = computed(
    () =>
      passkeySupport() === 'supported' &&
      this.securityConfig.config()?.passkeysAvailable === true &&
      !this.passkeyWithdrawn(),
  );
  /** 通行密钥登录在途（按钮发起，或从自动填充选中后提交断言）。 */
  protected readonly passkeyBusy = signal(false);
  /** 通行密钥在浏览器这一侧没成：按归类就地说明，不显示浏览器原文；改动输入或再试时清掉。 */
  protected readonly passkeyFailure = signal<WebAuthnFailure | null>(null);
  /** 进行中的条件式请求；换成按钮发起、进入第二步或离开页面时中止。 */
  private conditionalRequest: AbortController | null = null;

  constructor() {
    // 进入登录页时取当前注册策略，决定是否给出注册入口与通行密钥
    this.securityConfig.refresh();

    // 条件式界面：用户名框在场、入口可用、没有别的通行密钥请求在途时挂一个请求，等本人在自动填充里选中。
    // 仪式在取选项时绑定租户，换了租户就重新挂
    effect(() => {
      const ready =
        this.showPasskey() && !this.twoFactorPrompt() && !this.authBlocked() && !this.passkeyBusy();
      //#if (IncludeMultiTenancy)
      this.tenantContext.current();
      //#endif
      untracked(() => {
        this.stopConditionalRequest();
        if (ready) {
          void this.startConditionalRequest();
        }
      });
    });
    this.destroyRef.onDestroy(() => this.stopConditionalRequest());
  }

  ngOnInit(): void {
    // 普通登录清理旧主体；重新认证保留当前上下文并始终显示表单，凭据成功后再替换。
    // 清理会话属于改变会话状态的流程，不放构造函数。
    if (this.route.snapshot.queryParamMap.get('reauthenticate') !== 'true') {
      this.sessionContext.clear();
    }
  }

  async onSubmit() {
    // 租户上下文没定案就不发认证请求。按钮已经禁用，这里再挡一次是因为回车提交、
    // 以及探测在"表单填完、按钮刚点下"之间才失败的时序都绕不过表单事件。
    if (this.authBlocked()) {
      return;
    }

    if (this.loginForm().invalid()) {
      this.loginForm().markAsTouched();
      return;
    }

    this._isLoading.set(true);
    this.loginError.set(null);

    try {
      const { usernameOrEmail, password } = this.model();

      const loginInput = { usernameOrEmail, password };
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');

      const result = await lastValueFrom(this.authService.login(loginInput));
      const prompt = twoFactorPromptOf(result);
      if (prompt) {
        this.twoFactorPrompt.set(prompt);
        return;
      }

      await this.finishLogin(returnUrl);
    } catch (error) {
      if (isRejection(error)) {
        this.loginError.set(applicationErrorMessage(error));
        return;
      }
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('account.login.loginFailed'), {
        description: applicationErrorMessage(error),
      });
      //#else
      toast.error('Login failed', { description: applicationErrorMessage(error) });
      //#endif
    } finally {
      this._isLoading.set(false);
    }
  }

  /** 第二步通过、会话已下发：接着建立会话上下文并跳转。 */
  protected async onTwoFactorCompleted(): Promise<void> {
    this._isLoading.set(true);
    try {
      await this.finishLogin(
        this.route.snapshot.queryParamMap.get('returnUrl') ?? this.callbackReturnUrl ?? null,
      );
    } catch (error) {
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('account.login.loginFailed'), {
        description: applicationErrorMessage(error),
      });
      //#else
      toast.error('Login failed', { description: applicationErrorMessage(error) });
      //#endif
    } finally {
      this._isLoading.set(false);
    }
  }

  /** 会话已下发之后的共同收尾：取当前用户、建立会话上下文、提示并跳转。 */
  private async finishLogin(returnUrl: string | null): Promise<void> {
    // 此时凭据（含所需 MFA）已经通过，才能清理旧主体；加载失败也不能沿用旧权限与设置。
    this.sessionContext.clear();
    await lastValueFrom(this.authService.loadUser());

    // 受限会话（组织要求两步验证而本人尚未启用）：先去设置，别的页面都进不去；设置完成后回到原目标
    if (this.authService.currentUser()?.twoFactorSetupRequired) {
      await this.router.navigateByUrl(this.returnUrls.twoFactorSetup(returnUrl));
      return;
    }

    // 会话上下文在任何跳转前建立：旧主体已清空，空权限下 permissionGuard 会把深链登录踢到 403；
    // 设置也要就位，SPA 内跳转不会重跑应用初始化器。
    await this.sessionContext.establish();

    // 成功提示放在会话建立之后：它一旦失败就走 catch 弹「登录失败」，
    // 提前提示会让用户先看到成功、紧接着看到失败，而人还停在登录页。
    //#if (IncludeLocalization)
    toast.success(this.transloco.translate('account.login.loginSuccess'), {
      description: this.transloco.translate('account.login.welcomeBack'),
      duration: 3000,
    });
    //#else
    toast.success('Signed in successfully', { description: 'Welcome back!', duration: 3000 });
    //#endif

    // 有可接续的站内目标就回去（授权端点 /connect/* 整页回到服务端继续签发）；
    // 否则按权限跳转：拥有任一平台入口权限才进管理区，而不是按角色名或超管标志判断。
    await this.returnUrls.navigate(returnUrl, () =>
      this.router.navigate([
        this.authorizationService.canAccessPlatform() ? '/platform' : '/workspace',
      ]),
    );
  }

  /** 改动任一输入：上一次被拒的原因不再对应当前填写的内容。 */
  protected clearLoginError(): void {
    this.loginError.set(null);
    this.passkeyFailure.set(null);
  }

  /** 本人点"用通行密钥登录"：先中止条件式请求（浏览器同一时刻只认一个），再取选项、调起浏览器。 */
  protected async signInWithPasskey(): Promise<void> {
    if (this.passkeyBusy() || this.isLoading() || this.authBlocked()) {
      return;
    }

    this.stopConditionalRequest();
    this.passkeyBusy.set(true);
    this.loginError.set(null);
    this.passkeyFailure.set(null);
    try {
      const { options, ceremony } = await lastValueFrom(this.accountService.beginPasskeySignIn());
      const credential = await getPasskey(options);
      await this.completePasskeySignIn(ceremony ?? '', credential);
    } catch (error) {
      this.showPasskeyError(error);
    } finally {
      this.passkeyBusy.set(false);
    }
  }

  private async startConditionalRequest(): Promise<void> {
    const controller = new AbortController();
    this.conditionalRequest = controller;
    if (!(await isConditionalMediationAvailable()) || controller.signal.aborted) {
      return;
    }

    let ceremony: string;
    let credential: WebAuthnJson;
    try {
      const result = await lastValueFrom(this.accountService.beginPasskeySignIn());
      if (controller.signal.aborted) {
        return;
      }
      ceremony = result.ceremony ?? '';
      credential = await getPasskey(result.options, {
        mediation: 'conditional',
        signal: controller.signal,
      });
    } catch (error) {
      // 自动填充只是捷径：取不到选项、浏览器失败都不打扰本人，按钮照样可用；部署没配时收起入口
      if (!controller.signal.aborted && this.isPasskeyUnavailable(error)) {
        this.passkeyWithdrawn.set(true);
      }
      return;
    }
    if (this.conditionalRequest === controller) {
      this.conditionalRequest = null;
    }

    this.passkeyBusy.set(true);
    this.loginError.set(null);
    this.passkeyFailure.set(null);
    try {
      await this.completePasskeySignIn(ceremony, credential);
    } catch (error) {
      this.showPasskeyError(error);
    } finally {
      this.passkeyBusy.set(false);
    }
  }

  private stopConditionalRequest(): void {
    this.conditionalRequest?.abort();
    this.conditionalRequest = null;
  }

  /** 交回断言；通过即下发会话，之后与第二步通过后同一条收尾（不再要求第二步）。 */
  private async completePasskeySignIn(ceremony: string, credential: WebAuthnJson): Promise<void> {
    await lastValueFrom(this.accountService.completePasskeySignIn({ ceremony, credential }));
    await this.onTwoFactorCompleted();
  }

  private showPasskeyError(error: unknown): void {
    if (isWebAuthnError(error)) {
      this.passkeyFailure.set(classifyWebAuthnError(error));
      return;
    }
    if (this.isPasskeyUnavailable(error)) {
      this.passkeyWithdrawn.set(true);
    }
    if (isRejection(error)) {
      this.loginError.set(applicationErrorMessage(error));
      return;
    }
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('account.login.loginFailed'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error('Login failed', { description: applicationErrorMessage(error) });
    //#endif
  }

  private isPasskeyUnavailable(error: unknown): boolean {
    return (
      error instanceof ApplicationHttpError && error.code === API_ERROR_CODES.passkeyUnavailable
    );
  }

  //#if (IncludeMultiTenancy)
  // 租户选择：确认后写入本地上下文，登录请求由拦截器附租户提示头；不选即宿主登录。
  readonly tenantName = signal('');

  /** 主机名探测（与找回密码页共用）：定案前认证入口一律等待，见 {@link hostTenantProbe}。 */
  private readonly probe = hostTenantProbe();
  readonly hostProbe = this.probe.decision;
  /** 租户区的错误说明（词条键）：探测失败或域名指向的租户不可用。 */
  readonly tenantError = this.probe.error;
  readonly tenantLocked = this.probe.locked;

  /** 租户区不接受操作：探测未回来、失败或已定案；只有 `undecided` 开放手选。 */
  readonly tenantSelectionBlocked = computed(
    () => this.hostProbe() !== 'undecided' || this.tenantLocked(),
  );

  /**
   * 租户上下文未定案时认证入口一律等待。与 {@link tenantSelectionBlocked} 不同：已定案时
   * 不许选择但允许登录；探测未回来或失败时提交会落到与界面不一致的上下文。
   */
  readonly authBlocked = this.probe.blocked;

  /** 重试域名探测：瞬时故障不该只剩刷新整页（表单可能已填好）。 */
  retryHostProbe(): Promise<void> {
    return this.probe.retry();
  }

  async onConfirmTenant(): Promise<void> {
    const name = this.tenantName().trim();
    // 探测未回来 / 域名已定案时不接受手选：前者会被随后的探测结果覆盖，后者本就不该能改。
    if (!name || this.tenantSelectionBlocked()) {
      return;
    }

    this.tenantError.set(null);

    // 不向服务端确认这个租户是否存在：那会让任何人靠这个接口枚举租户。
    // 名字直接记进上下文；租户不存在或已停用时，请求会被中间件按统一的 404 挡住
    // （两种情形不区分，停用状态本身也是情报），不是由登录接口判凭据。
    this.tenantContext.set(name);
    this.tenantName.set('');
    // 上一个租户下的拒绝原因（凭据不对、已锁定）不再对应新的上下文
    this.loginError.set(null);
  }

  clearTenant(): void {
    // 与手选同一条闸门：清除也是一次手动改租户，探测未回来或失败时同样不该放行。
    if (this.tenantSelectionBlocked()) {
      return;
    }

    this.tenantContext.clear();
    this.tenantError.set(null);
    this.loginError.set(null);
  }

  /** 「切换租户」展开的输入框；默认收起，只显示当前租户一行。 */
  readonly tenantEditing = signal(false);

  /** 应用输入框里的租户：填了名称切到该租户，留空回到宿主。 */
  async applyTenantInput(): Promise<void> {
    if (this.tenantName().trim()) {
      await this.onConfirmTenant();
    } else {
      this.clearTenant();
    }
    this.tenantEditing.set(false);
  }
  //#else
  readonly authBlocked = signal(false);
  //#endif
}
/**
 * 服务端以 Problem Details 拒绝了这次填写（登录是写请求，5xx 已归为 `uncertain`，`problem` 只剩按内容拒绝）：
 * 原因就地显示；断网、服务故障与无法识别的响应不在此列。
 */
function isRejection(error: unknown): error is ApplicationHttpError {
  return error instanceof ApplicationHttpError && error.category === 'problem';
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.login.title': 'Welcome back',
  'account.login.tenant': 'Tenant',
  'account.login.tenantHost': 'Host',
  'account.login.tenantSwitch': 'Switch tenant',
  'account.login.tenantFromDomain': 'Determined by the site address.',
  'account.login.tenantPlaceholder': 'Tenant name, leave empty for host',
  'account.login.tenantConfirm': 'Confirm',
  'account.login.tenantResolving': 'Identifying tenant from the site address…',
  'account.login.tenantProbeRetry': 'Retry',
  'account.login.usernameOrEmail': 'Username or Email',
  'account.login.usernameOrEmailPlaceholder': 'Please enter your username or email',
  'account.login.password': 'Password',
  'account.login.passwordPlaceholder': 'Please enter your password',
  'account.login.forgotPassword': 'Forgot password?',
  'common.hidePassword': 'Hide password',
  'common.showPassword': 'Show password',
  'account.login.noAccount': "Don't have an account? ",
  'account.login.registerNow': 'Sign up',
  'account.login.submit': 'Sign in',
  'account.login.passkey': 'Sign in with a passkey',
  'account.login.passkeyCancelled': 'The passkey request was cancelled. Try again when ready.',
  'account.login.passkeyFailed':
    "The passkey couldn't be used. Try again, or sign in with your password.",
  //#if (ExternalLogin)
  'account.login.or': 'Or continue with',
  //#endif
  'account.login.testAccount': 'Development test account',
  'account.login.tenantUnavailable':
    'The tenant this address points to is unavailable. Contact your administrator.',
  'account.login.tenantProbeFailed':
    'Could not determine the tenant for this address. Check your connection and try again.',
};
//#endif
