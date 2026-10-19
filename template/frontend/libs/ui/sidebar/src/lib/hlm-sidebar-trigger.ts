import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucidePanelLeft } from '@ng-icons/lucide';
import { HlmButton, provideBrnButtonConfig } from '@spartan-ng/helm/button';
import { injectHlmA11yLabels } from '@spartan-ng/helm/utils';
import { HlmSidebarService } from './hlm-sidebar.service';

@Component({
  // eslint-disable-next-line @angular-eslint/component-selector
  selector: 'button[hlmSidebarTrigger]',
  exportAs: 'hlmSidebarTrigger',
  imports: [NgIcon],
  providers: [
    provideIcons({ lucidePanelLeft }),
    provideBrnButtonConfig({ variant: 'ghost', size: 'icon-sm' }),
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  hostDirectives: [{ directive: HlmButton, inputs: ['variant', 'size'] }],
  host: {
    'data-slot': 'sidebar-trigger',
    'data-sidebar': 'trigger',
    '(click)': '_onClick()',
  },
  template: `
    <ng-icon name="lucidePanelLeft" />
    <span class="sr-only">{{ srOnlyText() ?? a11y.toggleSidebar() }}</span>
  `,
})
export class HlmSidebarTrigger {
  private readonly _sidebarService = inject(HlmSidebarService);
  protected readonly a11y = injectHlmA11yLabels();

  // 未显式传入时取令牌里的译文，而不是写死英文：理由与取值位置见 HlmDialogContent.closeLabel
  public readonly srOnlyText = input<string | undefined>(undefined);

  protected _onClick(): void {
    this._sidebarService.toggleSidebar();
  }
}
