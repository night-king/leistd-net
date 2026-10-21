import { ChangeDetectionStrategy, Component, input } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { HlmSpinner } from '@spartan-ng/helm/spinner';
//#if (!IncludeLocalization)

import { englishText } from '../../utils/english-text';
//#endif

@Component({
  selector: 'app-dialog-loading',
  standalone: true,
  // prettier-ignore
  imports: [
    HlmSpinner,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './dialog-loading.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DialogLoading {
  // 调用方可显式传入文本；未传入时回退到默认加载文案。
  readonly text = input<string>();
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'app.startup.loading': 'Loading...',
};
//#endif
