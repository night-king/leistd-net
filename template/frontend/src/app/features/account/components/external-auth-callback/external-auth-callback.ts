import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { lastValueFrom } from 'rxjs';

import {
  ReauthenticateDialog,
  ReauthenticationProof,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import { injectReauthentication } from '../../../../core/components/reauthenticate-dialog/reauthentication';
import {
  ApplicationHttpError,
  applicationErrorMessage,
} from '../../../../core/errors/application-http-error';
import { ReturnUrlService } from '../../../../core/routing/return-url-service';
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { SessionContextService } from '../../../../core/services/session-context-service';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';
import { twoFactorPromptOf } from '../two-factor-challenge/two-factor-challenge';

/**
 * 外部登录回调：从路由取 provider（`/auth/external-callback/{provider}`）并读取完成意图，
 * 由后端消费短时外部票据完成登录或绑定，再按权限跳转。
 */
@Component({
  selector: 'app-external-auth-callback',
  // prettier-ignore
  imports: [
    HlmButton,
    HlmSpinner,
    ReauthenticateDialog,
    RouterLink,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './external-auth-callback.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExternalAuthCallback implements OnInit {
  private authService = inject(AuthService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly sessionContext = inject(SessionContextService);
  private accountService = inject(AccountService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private readonly returnUrls = inject(ReturnUrlService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /**
   * 失败说明：本端的说法存词条键、由模板按当前语言取，服务端下发的原因原样显示。
   * 存成已翻译的文字的话，停在这页切换语言时它不会跟着变。
   */
  protected readonly error = signal<{ key: string } | { text: string } | null>(null);

  /** 绑定时会话不够新：请本人确认身份，再用同一张外部票据重试。 */
  protected readonly reauth = injectReauthentication();
  /** 正在等待确认身份后重试的绑定；为 null 时对话框关闭不再触发离开。 */
  private pendingLinkProvider: string | null = null;

  ngOnInit(): void {
    this.processCallback();
  }

  private async processCallback(): Promise<void> {
    const params = this.route.snapshot.queryParamMap;
    const intent = params.get('intent') ?? 'login';
    const provider = this.route.snapshot.paramMap.get('provider');

    if (!provider) {
      this.error.set({ key: 'account.externalCallback.missingParams' });
      return;
    }

    // 用户在提供商处取消或协议校验失败时，后端带原因码回到这里，不签发外部票据，也不调用完成端点。
    const failure = params.get('error');
    if (failure) {
      const key =
        failure === 'cancelled'
          ? 'account.externalCallback.cancelled'
          : 'account.externalCallback.failed';
      if (intent === 'link') {
        //#if (IncludeLocalization)
        toast.error(this.transloco.translate(key));
        //#else
        toast.error(this.t(key));
        //#endif
        await this.returnToSecurity();
        return;
      }
      // 后退重放旧回调时原会话往往仍然有效：直接回到应用，不展示"登录失败"。
      if (await this.isSignedIn()) {
        await this.enterApplication();
        return;
      }
      this.error.set({ key });
      return;
    }

    // 查询参数仅选择完成端点意图，必须匹配后端保护的票据。
    if (intent === 'link') {
      await this.completeLink(provider);
      return;
    }

    try {
      // 后端验证并消费外部票据，决定最终会话或第二步凭据。
      const result = await lastValueFrom(this.accountService.externalLoginCallback(provider));

      // 需要第二步：会话还没下发，回登录页做第二步（凭据与可用方式走导航状态，不进地址栏）
      const twoFactorPrompt = twoFactorPromptOf(result);
      if (twoFactorPrompt) {
        await this.router.navigate(['/auth/login'], {
          state: { twoFactorPrompt, returnUrl: result.returnUrl },
        });
        return;
      }

      await lastValueFrom(this.authService.loadUser());
      await this.enterApplication(result?.returnUrl);
    } catch (err) {
      console.error('External login callback processing failed', err);
      // 业务拒绝（如"该邮箱已有账号，请先登录再绑定"）要让用户知道下一步怎么做，展示服务端下发的原因；
      // 其余失败只给通用提示（完成端点是写请求，5xx 已归为 `uncertain`）
      if (err instanceof ApplicationHttpError && err.category === 'problem') {
        this.error.set({ text: applicationErrorMessage(err) });
        return;
      }
      this.error.set({ key: 'account.externalCallback.failed' });
    }
  }

  /**
   * 建立会话上下文（权限 + 设置）并按权限跳转；调用前当前用户已加载。
   * 设置也必须在这里就位：SPA 内跳转不会重跑应用初始化器。
   */
  private async enterApplication(returnUrl?: string | null): Promise<void> {
    // 受限会话先去设置，原目标随之带过去，设置完成后回到那里
    if (this.authService.currentUser()?.twoFactorSetupRequired) {
      await this.router.navigateByUrl(this.returnUrls.twoFactorSetup(returnUrl));
      return;
    }
    await this.sessionContext.establish();
    await this.returnUrls.navigate(returnUrl, () =>
      this.router.navigate([
        this.authorizationService.canAccessPlatform() ? '/platform' : '/workspace',
      ]),
    );
  }

  /** 静默探测会话：401 不触发全局跳转，失败即视为未登录。 */
  private async isSignedIn(): Promise<boolean> {
    try {
      await lastValueFrom(this.authService.loadUser());
      return true;
    } catch {
      return false;
    }
  }

  /**
   * 完成绑定并回到「账户与安全」面板；失败也回去，由那里的列表反映实际状态。
   * 会话不够新时服务端不消费外部票据：留在这里请本人确认身份，带上交出的凭证重试；凭证被拒时留在对话框里
   * 就地说明，取消则回去。
   */
  private async completeLink(provider: string, proof?: ReauthenticationProof): Promise<void> {
    this.pendingLinkProvider = provider;
    try {
      await lastValueFrom(
        this.accountService.linkExternalLogin(provider, {
          password: proof?.password,
          twoFactorCode: proof?.twoFactorCode,
          passkey: proof?.passkey,
        }),
      );
      //#if (IncludeLocalization)
      toast.success(this.transloco.translate('account.externalLogins.linked'));
      //#else
      toast.success('Account linked');
      //#endif
    } catch (error) {
      if (this.reauth.handleError(error, proof, (next) => void this.completeLink(provider, next))) {
        return;
      }
      this.showLinkError(error);
    }
    this.pendingLinkProvider = null;
    this.reauth.complete();
    await this.returnToSecurity();
  }

  /** 本人关掉了确认身份的对话框：放弃这次绑定，回到「账户与安全」面板。 */
  protected onReauthVisibleChange(visible: boolean): void {
    this.reauth.setVisible(visible);
    if (!visible && this.pendingLinkProvider) {
      this.pendingLinkProvider = null;
      void this.returnToSecurity();
    }
  }

  /**
   * 本人选择重新登录：回来后落在「账户与安全」面板，从那里重新发起绑定。这张外部票据只属于这一次会话，
   * 回到回调页无法再用。
   */
  protected onSignInAgain(): void {
    this.pendingLinkProvider = null;
    this.reauth.signInAgain('/workspace/settings/security');
  }

  private showLinkError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error('Request failed', { description: applicationErrorMessage(error) });
    //#endif
  }

  private async returnToSecurity(): Promise<void> {
    // 回调页整页加载时启动流程不取当前用户（它按登录流程处理），这里补上再回设置页；
    // 取不到说明会话已不在，交给路由守卫带去登录页
    try {
      await lastValueFrom(this.authService.loadUser());
      await this.sessionContext.establish();
    } catch {
      // 由守卫处理
    }
    await this.router.navigate(['/workspace/settings/security']);
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.login.loginFailed': 'Sign-in failed',
  'account.externalCallback.processingAria': 'Completing sign-in',
  'account.externalCallback.processing': 'Processing third-party sign-in',
  'account.externalCallback.missingParams': 'Missing required callback parameters',
  'account.externalCallback.failed': 'Third-party sign-in failed. Please go back and try again.',
  'account.externalCallback.cancelled': 'Third-party sign-in was cancelled.',
  'account.externalCallback.backToLogin': 'Back to sign in',
  'account.externalLogins.linkReauthDescription':
    'Linking another account adds a way to sign in. Confirm it is you to continue.',
};
//#endif
