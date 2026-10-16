import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideSend } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { finalize } from 'rxjs/operators';

import { applicationErrorMessage } from '../../../../../../core/errors/application-http-error';
import { AuthService } from '../../../../../../core/services/auth-service';
import { SettingService } from '../../../../../../core/settings/setting-service';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif

/** 「邮件发送」面板底部：用当前生效的参数发一封测试邮件，失败时原样显示服务端给出的原因。 */
@Component({
  selector: 'app-email-test',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    HlmInput,
    HlmSpinner,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideSend })],
  templateUrl: './email-test.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EmailTest {
  private readonly settingService = inject(SettingService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 默认发给自己：最常见的用法就是"发一封给我看看收不收得到"。 */
  protected readonly to = signal(inject(AuthService).currentUser()?.email ?? '');
  protected readonly sending = signal(false);
  protected readonly canSend = computed(() => /^\S+@\S+\.\S+$/.test(this.to()) && !this.sending());

  protected send(): void {
    if (!this.canSend()) {
      return;
    }

    this.sending.set(true);
    this.settingService
      .sendTestEmail(this.to())
      .pipe(finalize(() => this.sending.set(false)))
      .subscribe({
        //#if (IncludeLocalization)
        next: () =>
          toast.success(this.transloco.translate('settings.emailTest.sent', { to: this.to() })),
        // 服务端的原因里已经带着"测试邮件发送失败"，标题不再重复一遍
        error: (error) =>
          toast.error(this.transloco.translate('common.requestError'), {
            description: applicationErrorMessage(error),
          }),
        //#else
        next: () => toast.success(this.t('settings.emailTest.sent', { to: this.to() })),
        // 服务端的原因里已经带着"测试邮件发送失败"，标题不再重复一遍
        error: (error) =>
          toast.error(this.t('common.requestError'), {
            description: applicationErrorMessage(error),
          }),
        //#endif
      });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'settings.emailTest.title': 'Send a test email',
  'settings.emailTest.description':
    'Uses the settings above as they are now. Save your changes first.',
  'settings.emailTest.recipient': 'Recipient',
  'settings.emailTest.send': 'Send test email',
  'settings.emailTest.sent': 'Test email sent to {{to}}',
  'common.requestError': 'Request error',
};
//#endif
