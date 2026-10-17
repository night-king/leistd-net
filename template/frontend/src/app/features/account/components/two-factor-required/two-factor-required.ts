import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideShieldCheck } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { lastValueFrom } from 'rxjs';

import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { ReturnUrlService } from '../../../../core/routing/return-url-service';
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { SessionContextService } from '../../../../core/services/session-context-service';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AuthShell } from '../auth-shell/auth-shell';
import { RecoveryCodes } from '../recovery-codes/recovery-codes';
import { TwoFactorSetup } from '../two-factor-setup/two-factor-setup';

/**
 * 组织要求两步验证而本人尚未启用时的设置页：受限会话调不了设置之外的接口，因此不进主布局；
 * 启用后同一个会话即不再受限，重建会话上下文后回到原目标（`returnUrl`），没有可接续的目标时按权限进入应用。
 */
@Component({
  selector: 'app-two-factor-required',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    AuthShell,
    RecoveryCodes,
    TwoFactorSetup,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideShieldCheck })],
  templateUrl: './two-factor-required.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TwoFactorRequired {
  private readonly authService = inject(AuthService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly sessionContext = inject(SessionContextService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly returnUrls = inject(ReturnUrlService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 启用成功后的恢复码；为 null 时还在设置那一步。 */
  protected readonly codes = signal<string[] | null>(null);

  /** 恢复码已保存：先刷新当前用户、权限与设置，再回到原目标或按权限进入应用。 */
  protected async continue(): Promise<void> {
    try {
      await lastValueFrom(this.authService.loadUser());
      await this.sessionContext.establish();
      await this.returnUrls.navigate(this.route.snapshot.queryParamMap.get('returnUrl'), () =>
        this.router.navigate([
          this.authorizationService.canAccessPlatform() ? '/platform' : '/workspace',
        ]),
      );
    } catch (error) {
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('common.requestError'), {
        description: applicationErrorMessage(error),
      });
      //#else
      toast.error(this.t('common.requestError'), { description: applicationErrorMessage(error) });
      //#endif
    }
  }

  protected logout(): void {
    this.authService.logout();
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.requestError': 'Request error',
  'account.twoFactorRequired.title': 'Turn on two-factor authentication',
  'account.twoFactorRequired.description':
    'Your organization requires two-factor authentication. Set it up to continue.',
  'account.twoFactorRequired.signOut': 'Sign out',
};
//#endif
