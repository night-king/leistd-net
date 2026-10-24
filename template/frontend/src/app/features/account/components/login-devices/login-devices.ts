import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideLogOut, lucideMonitor, lucideSmartphone, lucideTablet } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmItemImports } from '@spartan-ng/helm/item';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { firstValueFrom } from 'rxjs';

import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { StatusDot } from '../../../../shared/directives/status-dot';
import { UserSessionOutputDto } from '../../../../shared/dtos/account-security.dto';
import { formatAppDate } from '../../../../shared/pipes/app-date-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { DeviceKind, describeUserAgent } from '../../../../shared/utils/user-agent';
import { AccountService } from '../../services/account-service';

const DEVICE_ICONS: Record<DeviceKind, string> = {
  desktop: 'lucideMonitor',
  mobile: 'lucideSmartphone',
  tablet: 'lucideTablet',
};

/** 撤销"其他所有设备"时占用的忙碌标记，与单个会话的 Id 区分开。 */
const OTHERS = 'others';

/**
 * 「账户与安全」面板的一节：本人当前登录着的设备，可以让其中一台或除本机外的全部退出。
 *
 * 撤销对已发出的 Cookie 立即生效（服务端逐请求确认会话还在）。本机不给"退出"按钮——那是退出登录。
 */
@Component({
  selector: 'app-login-devices',
  // prettier-ignore
  imports: [
    StatusDot,
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmSpinner,
    ...HlmItemImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideLogOut, lucideMonitor, lucideSmartphone, lucideTablet })],
  templateUrl: './login-devices.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginDevices {
  private readonly accountService = inject(AccountService);
  private readonly confirmService = inject(ConfirmService);
  private readonly settingContext = inject(SettingContextService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** null 表示尚未取回。 */
  protected readonly sessions = signal<UserSessionOutputDto[] | null>(null);
  protected readonly loadFailed = signal(false);
  /** 正在撤销的会话 Id，或 {@link OTHERS}。 */
  protected readonly busy = signal<string | null>(null);
  protected readonly othersBusy = computed(() => this.busy() === OTHERS);

  protected readonly hasOthers = computed(() => (this.sessions() ?? []).some((s) => !s.isCurrent));

  /** 文案在模板里经 t 组装，这里只给出设备与时间等数据。 */
  protected readonly rows = computed(() =>
    (this.sessions() ?? []).map((session) => {
      const device = describeUserAgent(session.userAgent);
      const when = (value: string) =>
        formatAppDate(
          value,
          'short',
          this.settingContext.timeZone(),
          this.settingContext.displayLocale(),
        );

      return {
        id: session.id,
        isCurrent: session.isCurrent,
        icon: DEVICE_ICONS[device.kind],
        device: [device.browser, device.os].filter(Boolean).join(' · '),
        ipAddress: session.ipAddress,
        lastSeen: when(session.lastSeenTime),
        signedInAt: when(session.creationTime),
        impersonatorName: session.impersonatorName,
      };
    }),
  );

  constructor() {
    this.reload();
  }

  /** 重新取回设备列表。改密码会让其他设备退出，面板据此调用。 */
  reload(): void {
    this.loadFailed.set(false);
    this.accountService.getSessions().subscribe({
      next: (sessions) => this.sessions.set(sessions),
      error: () => this.loadFailed.set(true),
    });
  }

  protected async revoke(id: string): Promise<void> {
    if (this.busy() !== null) return;
    this.busy.set(id);

    try {
      const confirmed = await this.confirmService.open({
        //#if (IncludeLocalization)
        message: this.transloco.translate('account.sessions.revokeConfirm'),
        confirmText: this.transloco.translate('account.sessions.revoke'),
        //#else
        message: this.t('account.sessions.revokeConfirm'),
        confirmText: this.t('account.sessions.revoke'),
        //#endif
        variant: 'destructive',
      });
      if (!confirmed) {
        return;
      }

      try {
        await firstValueFrom(this.accountService.revokeSession(id));

        this.sessions.update((list) => list?.filter((s) => s.id !== id) ?? null);
        //#if (IncludeLocalization)
        toast.success(this.transloco.translate('account.sessions.revoked'));
        //#else
        toast.success(this.t('account.sessions.revoked'));
        //#endif
      } catch (error: unknown) {
        this.showError(error);
      }
    } finally {
      this.busy.set(null);
    }
  }

  protected async revokeOthers(): Promise<void> {
    if (this.busy() !== null) return;
    this.busy.set(OTHERS);

    try {
      const confirmed = await this.confirmService.open({
        //#if (IncludeLocalization)
        message: this.transloco.translate('account.sessions.revokeOthersConfirm'),
        confirmText: this.transloco.translate('account.sessions.revokeOthers'),
        //#else
        message: this.t('account.sessions.revokeOthersConfirm'),
        confirmText: this.t('account.sessions.revokeOthers'),
        //#endif
        variant: 'destructive',
      });
      if (!confirmed) {
        return;
      }

      try {
        const count = await firstValueFrom(this.accountService.revokeOtherSessions());

        this.sessions.update((list) => list?.filter((s) => s.isCurrent) ?? null);
        //#if (IncludeLocalization)
        toast.success(this.transloco.translate('account.sessions.revokedOthers', { count }));
        //#else
        toast.success(this.t('account.sessions.revokedOthers', { count }));
        //#endif
      } catch (error: unknown) {
        this.showError(error);
      }
    } finally {
      this.busy.set(null);
    }
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
  'account.sessions.header': 'Signed-in devices',
  'account.sessions.subtitle':
    "These devices are signed in to your account. If you don't recognize one, sign it out and change your password.",
  'account.sessions.current': 'This device',
  'account.sessions.unknownDevice': 'Unknown device',
  'account.sessions.ip': 'IP {{ip}}',
  'account.sessions.lastSeen': 'Last active {{time}}',
  'account.sessions.signedInAt': 'Signed in {{time}}',
  'account.sessions.impersonatedBy': 'Signed in by {{name}} via impersonation',
  'account.sessions.revoke': 'Sign out',
  'account.sessions.revokeOthers': 'Sign out other devices',
  'account.sessions.revokeConfirm': 'Sign this device out? It will need to sign in again.',
  'account.sessions.revokeOthersConfirm':
    'Sign out every device except this one? They will need to sign in again.',
  'account.sessions.revoked': 'The device has been signed out',
  'account.sessions.revokedOthers': 'Signed out {{count}} device(s)',
  'account.sessions.loadFailed': "Couldn't load your devices.",
};
//#endif
