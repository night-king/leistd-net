//#if (IncludeLocalization)
import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { TranslocoDirective, translateSignal } from '@jsverse/transloco';

import { LayoutService } from '../../../../core/services/layout-service';
//#else
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { LayoutService } from '../../../../core/services/layout-service';
import { englishText } from '../../../../shared/utils/english-text';
//#endif

@Component({
  selector: 'app-workspace-placeholder',
  standalone: true,
  //#if (IncludeLocalization)
  imports: [TranslocoDirective],
  //#endif
  templateUrl: './workspace-placeholder.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspacePlaceholder {
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);

  //#endif
  constructor() {
    const layoutService = inject(LayoutService);
    //#if (IncludeLocalization)
    const title = translateSignal('workspace.placeholder.title', {}, { scope: 'workspace' });
    effect(() => layoutService.title.set(title()));
    //#else
    layoutService.title.set('Example module');
    //#endif
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'workspace.placeholder.title': 'Example module',
  'workspace.placeholder.comingSoon':
    'A placeholder for your own business modules — replace this page and its menu entry under "Business".',
};
//#endif
