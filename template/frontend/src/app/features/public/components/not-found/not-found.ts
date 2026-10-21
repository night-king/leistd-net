import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideSearchX } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
//#if (!IncludeLocalization)

import { englishText } from '../../../../shared/utils/english-text';
//#endif

/** 404 页面：通配路由原地渲染，地址栏保留用户输入的地址。 */
@Component({
  selector: 'app-not-found',
  standalone: true,
  // prettier-ignore
  imports: [
    RouterLink,
    NgIcon,
    HlmButton,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideSearchX })],
  templateUrl: './not-found.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
//#if (IncludeLocalization)
export class NotFound {}
//#else
export class NotFound {
  protected readonly t = englishText(ENGLISH);
}
//#endif
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  title: 'Page not found',
  description: 'The page you are looking for does not exist or may have been moved.',
  backHome: 'Go home',
};
//#endif
