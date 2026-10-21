import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideLogOut } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { lastValueFrom } from 'rxjs';

import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { MOCKED_URL } from '../../../../core/mock/mocked-url';
import { AuthService } from '../../../../core/services/auth-service';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { LogoutConfirmationOutputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';
import { AuthShell } from '../auth-shell/auth-shell';

/**
 * 依赖方发起的退出、但无法确认指向当前会话（没有 id_token_hint，或 hint 属于别的会话）时，由用户确认。
 *
 * 地址里只有不透明的引用（request_uri 与受保护的确认凭据），不含 id_token。确认是一次本源整页表单 POST，
 * 带上服务端给的官方防伪令牌：退出完成后由协议端点把浏览器送回发起退出的应用。取消只是离开，会话保持。
 */
@Component({
  selector: 'app-logout-confirm',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    HlmSpinner,
    AuthShell,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideLogOut })],
  templateUrl: './logout-confirm.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LogoutConfirm implements OnInit {
  private readonly accountService = inject(AccountService);
  private readonly authService = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly isMockedUrl = inject(MOCKED_URL);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 核对结果；null 时还在核对（或核对请求失败，见 loadError）。 */
  protected readonly confirmation = signal<LogoutConfirmationOutputDto | null>(null);
  /** 核对请求本身失败（网络、5xx）时的说明：这不等于凭据无效，给出重试。 */
  protected readonly loadError = signal<string | null>(null);
  protected readonly submitting = signal(false);

  ngOnInit(): Promise<void> {
    return this.load();
  }

  protected async load(): Promise<void> {
    const query = this.route.snapshot.queryParamMap;
    const requestUri = query.get('request_uri');
    const confirmation = query.get('confirmation');
    if (!requestUri || !confirmation) {
      this.confirmation.set({ isValid: false });
      return;
    }
    this.loadError.set(null);
    try {
      this.confirmation.set(
        await lastValueFrom(this.accountService.getLogoutConfirmation(requestUri, confirmation)),
      );
    } catch (error) {
      this.loadError.set(applicationErrorMessage(error));
    }
  }

  protected confirm(): void {
    const info = this.confirmation();
    const query = this.route.snapshot.queryParamMap;
    if (!info?.isValid || !info.antiforgeryFieldName || !info.antiforgeryToken) {
      return;
    }
    this.submitting.set(true);
    if (this.isMockedUrl('/api/v1/auth/logout-confirmation')) {
      // 确认信息来自 Mock（防伪令牌是合成的）：不能提交真实协议端点，走既有的本地退出，确认后的会话状态与真实后端一致
      this.authService.logout();
      return;
    }
    // 整页 POST：退出完成后的跳转（回到发起退出的应用）由浏览器跟随，不经 XHR
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = '/connect/logout';
    for (const [name, value] of [
      ['request_uri', query.get('request_uri') ?? ''],
      ['confirmation', query.get('confirmation') ?? ''],
      [info.antiforgeryFieldName, info.antiforgeryToken],
    ]) {
      const input = document.createElement('input');
      input.type = 'hidden';
      input.name = name;
      input.value = value;
      form.appendChild(input);
    }
    document.body.appendChild(form);
    form.submit();
  }

  /** 取消：不退出，回到本应用首页。发起退出的应用已清掉的本地会话不会因此恢复。 */
  protected cancel(): void {
    void this.router.navigateByUrl('/');
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.logoutConfirm.title': 'Sign out?',
  'account.logoutConfirm.description': '{{application}} is asking to sign you out of this account.',
  'account.logoutConfirm.descriptionUnknown':
    'An application is asking to sign you out of this account.',
  'account.logoutConfirm.confirm': 'Sign out',
  'account.logoutConfirm.cancel': 'Stay signed in',
  'account.logoutConfirm.invalid':
    'This sign-out request has expired or belongs to another session. Sign out again from the application.',
  'account.logoutConfirm.backHome': 'Back to home',
  'account.logoutConfirm.loading': 'Checking the sign-out request...',
  'account.logoutConfirm.failed': 'The sign-out request could not be checked.',
  'account.logoutConfirm.retry': 'Try again',
};
//#endif
