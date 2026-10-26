import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideLink } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmItemImports } from '@spartan-ng/helm/item';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { finalize } from 'rxjs/operators';

import {
  ReauthenticateDialog,
  ReauthenticationProof,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import { injectReauthentication } from '../../../../core/components/reauthenticate-dialog/reauthentication';
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
import { AccountPreferencesService } from '../../../../core/services/account-preferences-service';
import { formatAppDate } from '../../../../shared/pipes/app-date-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { ExternalLoginsOutputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';

const PROVIDER_LABELS: Record<string, string> = { github: 'GitHub', google: 'Google' };

/**
 * 「账户与安全」面板的一节：已绑定的外部账号，可绑定或解绑。绑定走完整外部授权；没有密码时
 * 服务端拒绝解绑最后一个。解绑会少一种登录方式，会话不够新时服务端要求先确认身份。
 * 部署没有可用的外部登录时整节不出现。
 */
@Component({
  selector: 'app-external-logins',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    HlmSpinner,
    ReauthenticateDialog,
    ...HlmItemImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideLink })],
  templateUrl: './external-logins.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // 区块直接参与账户与安全面板的纵向间距；整块不渲染时不留空位
  host: { class: 'contents' },
})
export class ExternalLogins {
  private readonly accountService = inject(AccountService);
  private readonly confirmService = inject(ConfirmService);
  private readonly accountPreferences = inject(AccountPreferencesService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  protected readonly data = signal<ExternalLoginsOutputDto | null>(null);
  protected readonly loadFailed = signal(false);
  protected readonly busy = signal<string | null>(null);
  /** 解绑而会话不够新：确认身份后带上凭证重试。 */
  protected readonly reauth = injectReauthentication();

  /** 成功取到的目录为空：部署没有可用的外部登录。加载中与加载失败不算，失败要能看到并重试。 */
  protected readonly isEmpty = computed(
    () => !this.loadFailed() && this.data()?.providers.length === 0,
  );

  protected readonly rows = computed(() => {
    const data = this.data();
    if (!data) {
      return [];
    }

    const linkedCount = data.providers.filter((p) => p.link).length;
    return data.providers.map((p) => ({
      provider: p.provider,
      label: PROVIDER_LABELS[p.provider] ?? p.provider,
      link: p.link ?? null,
      // 文案在模板里经 t 组装，这里只给出绑定的账号名与时间
      linkedName: p.link ? (p.link.providerAccountLabel ?? p.link.providerEmail ?? '') : '',
      linkedAt: p.link
        ? formatAppDate(
            p.link.creationTime,
            'date',
            this.accountPreferences.timeZone(),
            this.accountPreferences.displayLocale(),
          )
        : '',
      // 没有密码时最后一个绑定就是唯一的登录方式
      canUnlink: !!p.link && (data.hasPassword || linkedCount > 1),
    }));
  });

  constructor() {
    this.reload();
  }

  /** 重新取绑定情况；设置口令之后由所在面板调用（有了口令，最后一个绑定也能解绑）。 */
  reload(): void {
    this.loadFailed.set(false);
    this.accountService.getExternalLogins().subscribe({
      next: (data) => this.data.set(data),
      error: () => this.loadFailed.set(true),
    });
  }

  protected link(provider: string): void {
    this.busy.set(provider);
    try {
      window.location.href = this.accountService.getExternalLinkUrl(provider);
    } catch (error) {
      this.busy.set(null);
      this.showError(error);
    }
  }

  protected onReauthenticationVisibleChange(visible: boolean): void {
    const saving = this.reauth.saving();
    this.reauth.setVisible(visible);
    if (!visible && !saving) this.busy.set(null);
  }
  protected async unlink(provider: string, id: string): Promise<void> {
    if (this.busy() !== null) return;
    this.busy.set(provider);
    let submitted = false;
    try {
      const providerLabel = PROVIDER_LABELS[provider] ?? provider;
      const confirmed = await this.confirmService.open({
        //#if (IncludeLocalization)
        message: this.transloco.translate('account.externalLogins.unlinkConfirm', {
          provider: providerLabel,
        }),
        confirmText: this.transloco.translate('account.externalLogins.unlink'),
        //#else
        message: this.t('account.externalLogins.unlinkConfirm', { provider: providerLabel }),
        confirmText: this.t('account.externalLogins.unlink'),
        //#endif
        variant: 'destructive',
      });
      if (!confirmed) {
        return;
      }

      this.unlinkConfirmed(provider, id);
      submitted = true;
    } finally {
      if (!submitted) this.busy.set(null);
    }
  }

  /** 先不带凭证解绑；服务端要求确认身份时弹出对话框，带上交出的凭证重试。 */
  private unlinkConfirmed(provider: string, id: string, proof?: ReauthenticationProof): void {
    this.busy.set(provider);
    this.accountService
      .unlinkExternalLogin(id, {
        password: proof?.password,
        twoFactorCode: proof?.twoFactorCode,
        passkey: proof?.passkey,
      })
      .pipe(
        finalize(() => {
          if (!this.reauth.visible()) this.busy.set(null);
        }),
      )
      .subscribe({
        next: () => {
          this.reauth.complete();
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('account.externalLogins.unlinked'));
          //#else
          toast.success(this.t('account.externalLogins.unlinked'));
          //#endif
          this.reload();
        },
        error: (error) => {
          if (
            !this.reauth.handleError(error, proof, (next) =>
              this.unlinkConfirmed(provider, id, next),
            )
          ) {
            this.showError(error);
          }
        },
      });
  }

  private showError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error(this.t('common.requestError'), { description: applicationErrorMessage(error) });
    //#endif
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.requestError': 'Request error',
  'common.retry': 'Retry',
  'account.externalLogins.header': 'Linked accounts',
  'account.externalLogins.subtitle':
    'Sign in with these accounts as well as your password. Keep at least one way to sign in.',
  'account.externalLogins.notLinked': 'Not linked',
  'account.externalLogins.linkedAs': 'Linked as {{name}} on {{time}}',
  'account.externalLogins.link': 'Link',
  'account.externalLogins.unlink': 'Unlink',
  'account.externalLogins.unlinkConfirm':
    'Unlink your {{provider}} account? You will no longer be able to sign in with it.',
  'account.externalLogins.unlinked': 'Account unlinked',
  'account.externalLogins.lastMethod': 'Your only way to sign in',
  'account.externalLogins.loadFailed': "Couldn't load linked accounts.",
  'account.externalLogins.unlinkReauthDescription':
    'Unlinking removes a way to sign in. Confirm it is you to continue.',
};
//#endif
