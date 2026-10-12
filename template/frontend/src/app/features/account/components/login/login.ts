// prettier-ignore
import {
  ChangeDetectionStrategy, Component, inject, OnInit, signal,
  //#if (IncludeMultiTenancy)
  computed,
  //#endif
} from '@angular/core';
import { form, minLength, maxLength, required, FormField } from '@angular/forms/signals';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import { lucideBuilding2, lucideInfo, lucideEye, lucideEyeOff } from '@ng-icons/lucide';
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

// prettier-ignore
import {
  applicationErrorMessage,
  //#if (ExternalLogin)
  ApplicationHttpError,
  //#endif
} from '../../../../core/errors/application-http-error';
import { MOCKED_URL } from '../../../../core/mock/mocked-url';
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { SessionContextService } from '../../../../core/services/session-context-service';
//#if (IncludeMultiTenancy)
import { TenantContextService } from '../../../../core/services/tenant-context-service';
//#endif
import { PASSWORD_MAX_LENGTH } from '../../../../core/validation/password-rule';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
//#if (IncludeMultiTenancy)
import { HostTenantDecision } from '../../dtos/tenant-by-host.dto';
//#endif
import { AccountService } from '../../services/account-service';
import { AuthShell } from '../auth-shell/auth-shell';
import { TwoFactorChallenge } from '../two-factor-challenge/two-factor-challenge';

// GitHub 品牌图标（lucide 已下架品牌 logo，用官方 SVG path 自定义注入）
const githubIcon =
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor"><path d="M12 .5C5.37.5 0 5.87 0 12.5c0 5.3 3.44 9.8 8.2 11.39.6.11.82-.26.82-.58v-2.03c-3.34.73-4.04-1.61-4.04-1.61-.55-1.39-1.34-1.76-1.34-1.76-1.09-.75.08-.73.08-.73 1.2.08 1.84 1.24 1.84 1.24 1.07 1.83 2.81 1.3 3.5.99.11-.78.42-1.3.76-1.6-2.67-.3-5.47-1.33-5.47-5.93 0-1.31.47-2.38 1.24-3.22-.13-.31-.54-1.52.11-3.18 0 0 1.01-.32 3.3 1.23a11.5 11.5 0 0 1 6 0c2.29-1.55 3.3-1.23 3.3-1.23.65 1.66.24 2.87.12 3.18.77.84 1.23 1.91 1.23 3.22 0 4.61-2.81 5.63-5.49 5.93.43.37.82 1.1.82 2.22v3.29c0 .32.22.7.83.58A12 12 0 0 0 24 12.5C24 5.87 18.63.5 12 .5z"/></svg>';

@Component({
  selector: 'app-login',
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
    TwoFactorChallenge,
  ],
  // prettier-ignore
  providers: [
    provideIcons({
      lucideBuilding2,
      lucideInfo,
      lucideEye,
      lucideEyeOff,
      github: githubIcon,
    }),
  ],
  templateUrl: './login.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Login implements OnInit {
  private accountService = inject(AccountService);
  private authService = inject(AuthService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly sessionContext = inject(SessionContextService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
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
  //#if (ExternalLogin)

  /** 部署已配置的外部登录提供商；null 表示还在加载，此时不显示任何入口。 */
  protected readonly externalProviders = signal<readonly string[] | null>(null);
  protected readonly externalProvidersFailed = signal(false);
  /** 5xx 失败时带上本地化的追踪 ID，供支持人员关联服务端日志。 */
  protected readonly externalProvidersTrace = signal<string | null>(null);
  //#endif

  // 登录接口由 Mock 应答时才提示演示账号：只 Mock 了别的模块时，演示账号登不进真实后端
  public readonly isMockEnabled = signal(inject(MOCKED_URL)(AuthService.loginUrl));

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
   * 第二步凭据：密码已通过、尚待验证码。有值时登录页换成验证码那一步。
   *
   * 外部登录回调遇到已启用两步验证的账号时，经导航状态把凭据带过来（不放进地址栏）。
   */
  private readonly callbackReturnUrl = (
    this.router.currentNavigation()?.extras.state as { returnUrl?: string } | undefined
  )?.returnUrl;

  protected readonly twoFactorToken = signal<string | null>(
    (this.router.currentNavigation()?.extras.state as { twoFactorToken?: string } | undefined)
      ?.twoFactorToken ?? null,
  );

//#if (IncludeMultiTenancy || ExternalLogin)
  constructor() {
//#if (IncludeMultiTenancy)
    // 子域名部署下按主机名把租户定住，用户完全不必填；未命中则保持原状（上次记住的或空白）。
    void this.resolveTenantFromHost();
//#endif
    //#if (ExternalLogin)
    this.loadExternalProviders();
    //#endif
  }

//#endif
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

    try {
      const { usernameOrEmail, password } = this.model();

      const loginInput = { usernameOrEmail, password };
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');

      const result = await lastValueFrom(this.authService.login(loginInput));
      if (result?.requiresTwoFactor && result.twoFactorToken) {
        this.twoFactorToken.set(result.twoFactorToken);
        return;
      }

      await this.finishLogin(returnUrl);
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

    // 受限会话（组织要求两步验证而本人尚未启用）：先去设置，别的页面都进不去
    if (this.authService.currentUser()?.twoFactorSetupRequired) {
      await this.router.navigate(['/auth/two-factor-setup']);
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

    if (this.isSafeLocalReturnUrl(returnUrl)) {
      //#if (OpenIddictServer)
      // 授权端点把未登录的授权请求送来这里；登录后整页回到服务端的授权端点继续签发，它不是前端路由
      if (returnUrl.startsWith('/connect/')) {
        window.location.href = returnUrl;
        return;
      }
      //#endif
      await this.router.navigateByUrl(returnUrl);
      return;
    }

    // 按权限跳转：拥有任一平台入口权限才进管理区，而不是按角色名或超管标志判断。
    if (this.authorizationService.canAccessPlatform()) {
      this.router.navigate(['/platform']);
    } else {
      this.router.navigate(['/workspace']);
    }
  }

  private isSafeLocalReturnUrl(returnUrl: string | null): returnUrl is string {
    return (
      !!returnUrl &&
      returnUrl.startsWith('/') &&
      !returnUrl.startsWith('//') &&
      !returnUrl.includes('://') &&
      !returnUrl.includes('\\')
    );
  }

  //#if (IncludeMultiTenancy)
  // 租户选择：确认后写入本地上下文，登录请求由拦截器附租户提示头；不选即宿主登录。
  readonly tenantName = signal('');
  /** 租户区的错误说明，存词条键、由模板按当前语言取：存成文字的话切换语言时它不会跟着变。 */
  readonly tenantError = signal<string | null>(null);

  /**
   * 主机名探测的进度与结果：
   * - `pending` 未回来：定案会覆盖当前上下文，租户区与认证入口都不放行；
   * - `tenant` / `host` 域名已定案，不能再改；
   * - `undecided` 域名不表态（未配置子域名格式），交回用户手填；
   * - `failed` 探测没能完成（租户不可用时中间件返回 404，或网络故障），不能当作 `undecided`。
   */
  readonly hostProbe = signal<HostTenantDecision | 'pending' | 'failed'>('pending');

  /**
   * 租户由域名定案（指向租户或宿主）时锁定：服务端按主机名解析且不允许请求头改写，
   * 放开选择会让界面显示的租户与请求实际落到的上下文不一致。
   */
  readonly tenantLocked = computed(() => {
    const decision = this.hostProbe();
    return decision === 'host' || decision === 'tenant';
  });

  /** 租户区不接受操作：探测未回来、失败或已定案；只有 `undecided` 开放手选。 */
  readonly tenantSelectionBlocked = computed(
    () => this.hostProbe() !== 'undecided' || this.tenantLocked(),
  );

  /**
   * 租户上下文未定案时认证入口一律等待。与 {@link tenantSelectionBlocked} 不同：已定案时
   * 不许选择但允许登录；探测未回来或失败时提交会落到与界面不一致的上下文。
   */
  readonly authBlocked = computed(
    () => this.hostProbe() === 'pending' || this.hostProbe() === 'failed',
  );

  /** 启动时按主机名探测租户。宿主定案与不表态分开处理，否则宿主域上会残留上次记住的租户。 */
  private async resolveTenantFromHost(): Promise<void> {
    try {
      const result = await lastValueFrom(this.accountService.getTenantByHost());
      switch (result.decision) {
        case 'tenant':
          // 租户不存在或已停用时 tenant 为空：清掉记住的那个，并保持锁定，
          // 让界面提示这个域名不可用，而不是让人换一个反正会被覆盖的租户。
          if (result.tenant) {
            this.tenantContext.set(result.tenant.name);
          } else {
            this.tenantContext.clear();
            this.tenantError.set('account.login.tenantUnavailable');
          }
          break;

        case 'host':
          // 受管域但不指向租户：服务端会按宿主处理，记住的租户必须清掉。
          this.tenantContext.clear();
          break;

        case 'undecided':
          // 域名不表态：保持现状（上次记住的租户，或空白待填）。
          break;
      }

      this.hostProbe.set(result.decision);
    } catch {
      // 未配置子域名格式的部署正常返回 undecided，走不到这里；失败只能是租户解析不了（404）
      // 或后端不可达，因此不开放手选也不放行登录，只给原因和重试。
      this.hostProbe.set('failed');
      this.tenantError.set('account.login.tenantProbeFailed');
    }
  }

  /** 重试域名探测：瞬时故障不该只剩刷新整页（表单可能已填好）。 */
  async retryHostProbe(): Promise<void> {
    if (this.hostProbe() !== 'failed') {
      return;
    }

    this.hostProbe.set('pending');
    this.tenantError.set(null);
    await this.resolveTenantFromHost();
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
  }

  clearTenant(): void {
    // 与手选同一条闸门：清除也是一次手动改租户，探测未回来或失败时同样不该放行。
    if (this.tenantSelectionBlocked()) {
      return;
    }

    this.tenantContext.clear();
    this.tenantError.set(null);
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
  //#if (ExternalLogin)

  /** 读取已配置的提供商。失败单独提示并可重试，不当作"未配置"；本地登录不受影响。 */
  protected loadExternalProviders(): void {
    this.externalProvidersFailed.set(false);
    this.externalProvidersTrace.set(null);
    this.accountService.getExternalLoginProviders().subscribe({
      next: (result) => this.externalProviders.set(result.providers),
      error: (error: unknown) => {
        this.externalProvidersTrace.set(
          error instanceof ApplicationHttpError && error.status >= 500 && error.traceId
            ? `${error.traceIdLabel}: ${error.traceId}`
            : null,
        );
        this.externalProvidersFailed.set(true);
      },
    });
  }

  protected hasExternalProvider(provider: string): boolean {
    return this.externalProviders()?.includes(provider) ?? false;
  }

  /** 页面只内置 GitHub、Google 入口；目录里只有其他提供商时不显示空的分隔区。 */
  protected hasRenderableExternalProvider(): boolean {
    return this.hasExternalProvider('github') || this.hasExternalProvider('google');
  }

  loginWithGitHub() {
    this.loginWithExternalProvider('github', 'GitHub');
  }

  loginWithGoogle() {
    this.loginWithExternalProvider('google', 'Google');
  }

  private loginWithExternalProvider(provider: 'github' | 'google', label: string) {
    // 与本地登录同一条约束：第三方回调最终也落在按主机名解析出的那个上下文里。
    if (this.authBlocked()) {
      return;
    }

    this._isLoading.set(true);
    try {
      window.location.href = this.accountService.getExternalLoginUrl(
        provider,
        this.route.snapshot.queryParamMap.get('returnUrl'),
      );
    } catch (error) {
      console.error(`${label} login failed`, error);
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('account.login.loginFailed'), {
        description: this.transloco.translate('account.login.externalLoginFailed', {
          provider: label,
        }),
        duration: 3000,
      });
      //#else
      toast.error('Sign-in failed', {
        description: `Unable to connect to the ${label} sign-in service, please try again later`,
        duration: 3000,
      });
      //#endif
      this._isLoading.set(false);
    }
  }
  //#endif
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
  'common.hidePassword': 'Hide password',
  'common.showPassword': 'Show password',
  'account.login.noAccount': "Don't have an account?",
  'account.login.registerNow': 'Sign up',
  'account.login.submit': 'Sign In',
  'account.login.or': 'Or sign in with your account',
  'account.login.testAccount': 'Development test account',
  'account.login.tenantUnavailable':
    'The tenant this address points to is unavailable. Contact your administrator.',
  'account.login.tenantProbeFailed':
    'Could not determine the tenant for this address. Check your connection and try again.',
  //#if (ExternalLogin)
  'account.login.externalProvidersLoadFailed': 'Third-party sign-in options could not be loaded.',
  'common.retry': 'Retry',
  //#endif
};
//#endif
