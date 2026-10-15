import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheck, lucideCopy, lucideDownload, lucideTriangleAlert } from '@ng-icons/lucide';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';

import { injectCopyToClipboard } from '../../../../shared/utils/clipboard';
import { saveBlob } from '../../../../shared/utils/download-file';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif

/**
 * 一次性展示新生成的恢复码，并提供复制与下载。
 *
 * 明文只在这里出现一次，服务端只存摘要；"我已保存"之前不收起，免得人还没抄下就没了。
 */
@Component({
  selector: 'app-recovery-codes',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    ...HlmAlertImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideCheck, lucideCopy, lucideDownload, lucideTriangleAlert })],
  templateUrl: './recovery-codes.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecoveryCodes {
  private readonly clipboard = injectCopyToClipboard();
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly codes = input.required<string[]>();
  /** 用户确认已经保存。 */
  readonly done = output<void>();

  protected readonly copied = this.clipboard.copied;

  // 剪贴板不可用时仍可手动选中或下载
  protected copy(): void {
    void this.clipboard.copy(this.codes().join('\n'));
  }

  protected download(): void {
    saveBlob(
      new Blob([`${this.codes().join('\n')}\n`], { type: 'text/plain' }),
      'recovery-codes.txt',
    );
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.twoFactor.recoveryCodesWarning':
    'Save these recovery codes somewhere safe. Each one signs you in once if you lose your phone. They will not be shown again.',
  'account.twoFactor.copyCodes': 'Copy',
  'account.twoFactor.copied': 'Copied',
  'account.twoFactor.downloadCodes': 'Download',
  'account.twoFactor.savedCodes': "I've saved them",
};
//#endif
