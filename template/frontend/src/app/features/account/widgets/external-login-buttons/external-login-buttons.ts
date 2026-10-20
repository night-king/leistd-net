import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';

import {
  ApplicationHttpError,
  applicationErrorMessage,
} from '../../../../core/errors/application-http-error';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';

// GitHub 品牌图标（lucide 已下架品牌 logo，用官方 SVG path 自定义注入）
const githubIcon =
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor"><path d="M12 .5C5.37.5 0 5.87 0 12.5c0 5.3 3.44 9.8 8.2 11.39.6.11.82-.26.82-.58v-2.03c-3.34.73-4.04-1.61-4.04-1.61-.55-1.39-1.34-1.76-1.34-1.76-1.09-.75.08-.73.08-.73 1.2.08 1.84 1.24 1.84 1.24 1.07 1.83 2.81 1.3 3.5.99.11-.78.42-1.3.76-1.6-2.67-.3-5.47-1.33-5.47-5.93 0-1.31.47-2.38 1.24-3.22-.13-.31-.54-1.52.11-3.18 0 0 1.01-.32 3.3 1.23a11.5 11.5 0 0 1 6 0c2.29-1.55 3.3-1.23 3.3-1.23.65 1.66.24 2.87.12 3.18.77.84 1.23 1.91 1.23 3.22 0 4.61-2.81 5.63-5.49 5.93.43.37.82 1.1.82 2.22v3.29c0 .32.22.7.83.58A12 12 0 0 0 24 12.5C24 5.87 18.63.5 12 .5z"/></svg>';

/**
 * 外部登录入口（登录页与注册页共用）：按部署已登记的提供商渲染 GitHub / Google 按钮，加载失败单独提示并可重试，
 * 不当作"未配置"；点击后整页跳到服务端的授权挑战。首次外部登录即建账号，所以注册页也放同一组入口。
 *
 * 租户上下文由 `AccountService` 从 `TenantContextService` 写进挑战地址；父页面在上下文未定案（如登录页的主机名
 * 探测未回来）或自身忙碌时传 `blocked`，此时按钮禁用、点击也不跳转。回调与两步验证沿用现有外部回调流程。
 */
@Component({
  selector: 'app-external-login-buttons',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ github: githubIcon })],
  templateUrl: './external-login-buttons.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // 按钮行与分隔线直接参与父页面的纵向间距；没有可显示的入口时不留空位
  host: { class: 'contents' },
})
export class ExternalLoginButtons {
  private readonly accountService = inject(AccountService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 外部登录完成后回到的地址（OIDC 授权回跳等），由父页面从查询参数取。 */
  readonly returnUrl = input<string | null>(null);
  /** 父页面不接受认证（租户上下文未定案、正在提交）时为 true。 */
  readonly blocked = input(false);
  /** 表单与下方入口之间分隔线上的文字，如"或使用以下方式登录""或使用以下方式注册"。 */
  readonly dividerLabel = input.required<string>();

  /** 部署已配置的外部登录提供商；null 表示还在加载，此时不显示任何入口。 */
  protected readonly providers = signal<readonly string[] | null>(null);
  protected readonly loadFailed = signal(false);
  /** 归一化后的失败说明（按分类的通用文案或服务端原因，有追踪 ID 时附上），供用户反馈与支持人员关联日志。 */
  protected readonly failureDetail = signal<string | null>(null);
  /** 已发起整页跳转：在页面离开前禁用按钮，避免重复点击。 */
  protected readonly redirecting = signal(false);

  constructor() {
    this.loadProviders();
  }

  /** 读取已配置的提供商。失败单独提示并可重试；本地登录、注册不受影响。 */
  protected loadProviders(): void {
    this.loadFailed.set(false);
    this.failureDetail.set(null);
    this.accountService.getExternalLoginProviders().subscribe({
      next: (result) => this.providers.set(result.providers),
      error: (error: unknown) => {
        // 只展示归一化过的错误；其他异常的 message 可能带内部细节
        this.failureDetail.set(
          error instanceof ApplicationHttpError ? applicationErrorMessage(error) : null,
        );
        this.loadFailed.set(true);
      },
    });
  }

  protected has(provider: string): boolean {
    return this.providers()?.includes(provider) ?? false;
  }

  /** 页面只内置 GitHub、Google 入口；目录里只有其他提供商时不显示空的分隔区。 */
  protected hasRenderable(): boolean {
    return this.has('github') || this.has('google');
  }

  protected start(provider: 'github' | 'google', label: string): void {
    if (this.blocked() || this.redirecting()) {
      return;
    }

    this.redirecting.set(true);
    try {
      window.location.href = this.accountService.getExternalLoginUrl(provider, this.returnUrl());
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
      this.redirecting.set(false);
    }
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.login.externalProvidersLoadFailed': 'Third-party sign-in options could not be loaded.',
  'common.retry': 'Retry',
};
//#endif
