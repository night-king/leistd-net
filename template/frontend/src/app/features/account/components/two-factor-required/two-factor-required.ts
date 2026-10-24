import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  signal,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideFingerprint, lucideShieldCheck } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { lastValueFrom } from 'rxjs';

import { ReauthenticateDialog } from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { ReturnUrlService } from '../../../../core/routing/return-url-service';
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { SecurityConfigService } from '../../../../core/services/security-config-service';
import { SessionContextService } from '../../../../core/services/session-context-service';
import { passkeySupport } from '../../../../core/webauthn/webauthn';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { PasskeyRegistrationOutputDto } from '../../dtos/account.dto';
import { injectPasskeyRegistration } from '../../utils/passkey-registration';
import { AuthShell } from '../auth-shell/auth-shell';
import { RecoveryCodes } from '../recovery-codes/recovery-codes';
import { TwoFactorSetup } from '../two-factor-setup/two-factor-setup';

/**
 * 组织要求两步验证而本人尚未启用时的设置页：受限会话调不了设置之外的接口，因此不进主布局；
 * 启用后同一个会话即不再受限，重建会话上下文后回到原目标（`returnUrl`），没有可接续的目标时按权限进入应用。
 *
 * 部署配了通行密钥、浏览器也支持时，首选"创建通行密钥"（一步完成、抗钓鱼，满足组织要求），
 * 也可以改用身份验证器；否则只给身份验证器的设置。两条路都在第一个独立因子时发下恢复码，先展示再离开。
 */
@Component({
  selector: 'app-two-factor-required',
  // prettier-ignore
  imports: [
    NgIcon,
    ...HlmAlertImports,
    HlmButton,
    HlmSpinner,
    AuthShell,
    ReauthenticateDialog,
    RecoveryCodes,
    TwoFactorSetup,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideFingerprint, lucideShieldCheck })],
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
  private readonly securityConfig = inject(SecurityConfigService);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 启用成功后的恢复码；为 null 时还在设置那一步。 */
  protected readonly codes = signal<string[] | null>(null);

  /** 部署配了通行密钥的依赖方，浏览器也能用：首选创建通行密钥。 */
  protected readonly passkeyOffered = computed(
    () =>
      passkeySupport() === 'supported' && this.securityConfig.config()?.passkeysAvailable === true,
  );
  /** 本人改用身份验证器。 */
  protected readonly useAuthenticator = signal(false);
  protected readonly method = computed(() =>
    this.passkeyOffered() && !this.useAuthenticator() ? 'passkey' : 'authenticator',
  );

  protected readonly registration = injectPasskeyRegistration({
    text: (key) => this.text(key),
    registered: (result) => this.onPasskeyRegistered(result),
    failed: (error) => this.showError(error),
    awaitingGesture: () =>
      afterNextRender(() => this.document.getElementById('two-factor-required-passkey')?.focus(), {
        injector: this.injector,
      }),
  });

  constructor() {
    // 取当前部署是否配了通行密钥；取不到时按没配处理，只给身份验证器
    this.securityConfig.refresh();
  }

  /** 首选创建通行密钥：确认身份之后停在"创建"这一步，本人再点一次。 */
  protected createPasskey(): void {
    if (this.registration.awaitingGesture()) {
      this.registration.continue();
      return;
    }
    this.registration.start();
  }

  protected switchMethod(useAuthenticator: boolean): void {
    this.registration.cancel();
    this.useAuthenticator.set(useAuthenticator);
  }

  // 第一个独立因子一定带着恢复码；万一没有就直接继续，不展示一个空的恢复码页
  private onPasskeyRegistered(result: PasskeyRegistrationOutputDto): void {
    if (result.recoveryCodes.length > 0) {
      this.codes.set(result.recoveryCodes);
      return;
    }
    void this.continue();
  }

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
      this.showError(error);
    }
  }

  private showError(error: unknown): void {
    toast.error(this.text('common.requestError'), { description: applicationErrorMessage(error) });
  }

  private text(key: string): string {
    //#if (IncludeLocalization)
    return this.transloco.translate(key);
    //#else
    return this.t(key);
    //#endif
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
  'account.twoFactorRequired.passkeyHint':
    'Use your fingerprint, face or screen lock. It takes one step and also signs you in without a password next time.',
  'account.twoFactorRequired.createPasskey': 'Create a passkey',
  'account.twoFactorRequired.continueHint':
    'Identity confirmed. Create the passkey on this device to finish.',
  'account.twoFactorRequired.useAuthenticator': 'Use an authenticator app instead',
  'account.twoFactorRequired.usePasskey': 'Create a passkey instead',
  'account.twoFactorRequired.reauthDescription':
    'Confirm it is you before adding a passkey to your account.',
  'account.passkeys.currentDevice': 'This device',
  'account.passkeys.cancelled': 'The passkey request was cancelled.',
  'account.passkeys.alreadyRegistered': 'This device already has a passkey for your account.',
  'account.passkeys.failed': "The passkey request didn't work. Try again.",
};
//#endif
