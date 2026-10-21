// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  //#if (IncludeLocalization)
  inject,
  //#endif
  input,
  model,
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheck, lucideCopy, lucideTriangleAlert } from '@ng-icons/lucide';
import { BrnDialogState } from '@spartan-ng/brain/dialog';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

import { injectCopyToClipboard } from '../../../../../../shared/utils/clipboard';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import { OpenApplicationOutputDto } from '../../../../dtos/open-application.dto';

/** 揭示密钥的起因：新建 Confidential 客户端，或重置已有客户端的密钥。 */
export type SecretRevealKind = 'created' | 'reset';

/**
 * 一次性揭示 Client Secret 的弹窗（重置 / 新建后复用同一实例）。
 * 展示所属应用、安全告警、Client ID 与 Secret 明文及各自的复制按钮；关闭后明文不再展示。
 */
@Component({
  selector: 'app-secret-reveal-dialog',
  standalone: true,
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    ...HlmDialogImports,
    ...HlmTooltipImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideCheck, lucideCopy, lucideTriangleAlert })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './secret-reveal-dialog.html',
})
export class SecretRevealDialog {
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif
  protected readonly clientIdClipboard = injectCopyToClipboard();
  protected readonly secretClipboard = injectCopyToClipboard();
  readonly visible = model(false);
  readonly secret = input('');
  readonly kind = input<SecretRevealKind>('created');
  /** 密钥所属应用；未登记显示名时以 Client ID 作名称。 */
  readonly application = input<Pick<OpenApplicationOutputDto, 'clientId' | 'displayName'> | null>(
    null,
  );
  protected readonly applicationName = computed(() => {
    const application = this.application();
    return application?.displayName || application?.clientId || '';
  });

  onStateChange(state: BrnDialogState): void {
    this.visible.set(state === 'open');
  }

  copy(): void {
    const value = this.secret();
    if (!value) {
      return;
    }
    void this.secretClipboard.copy(value).then((copied) => {
      if (!copied) {
        return;
      }

      //#if (IncludeLocalization)
      toast.success(this.transloco.translate('common.success'), {
        description: this.transloco.translate('openApp.toast.secretCopied'),
      });
      //#else
      toast.success('Success', { description: 'Secret copied' });
      //#endif
    });
  }

  copyClientId(): void {
    const value = this.application()?.clientId;
    if (!value) {
      return;
    }
    void this.clientIdClipboard.copy(value).then((copied) => {
      if (!copied) {
        return;
      }

      //#if (IncludeLocalization)
      toast.success(this.transloco.translate('common.success'), {
        description: this.transloco.translate('openApp.toast.clientIdCopied'),
      });
      //#else
      toast.success('Success', { description: 'Client ID copied' });
      //#endif
    });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'openApp.secret.resetHeader': 'Client secret reset',
  'openApp.secret.createdHeader': 'Client credentials created',
  'openApp.secret.applicationLabel': 'Application:',
  'openApp.secret.warning':
    'Please copy and store it securely now; the secret will not be shown in plain text again.',
  'openApp.field.clientId': 'Client ID',
  'openApp.secret.copyClientId': 'Copy Client ID',
  'openApp.secret.clientSecret': 'Client secret',
  'openApp.secret.copySecret': 'Copy client secret',
  'common.close': 'Close',
  'openApp.secret.copyAndClose': 'Copy and close',
};
//#endif
