import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { SettingsPanelLink, SettingsShell } from './settings-shell';

@Component({
  selector: 'app-test-settings-page',
  imports: [SettingsShell],
  template: `
    <app-settings-shell
      heading="System settings"
      description="Defaults for this tenant."
      navLabel="Settings panels"
      openNavLabel="Open settings panels"
      [panels]="panels"
    />
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class SettingsPage {
  readonly panels: SettingsPanelLink[] = [
    { path: 'email', label: 'Email', icon: 'lucideMenu', description: 'Outgoing mail.' },
  ];
}

@Component({
  selector: 'app-test-settings-panel',
  template: '<p>panel</p>',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class EmailPanel {}

/**
 * 外壳在布局里：页面的 h1 由布局页头给出，外壳的可见标题是 h2、面板标题 h3，面板内的分区再往下；
 * 布局已有 main，外壳内容区不再套一层。窄屏抽屉的名称取外壳标题。
 */
describe('SettingsShell headings', () => {
  async function render(): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: 'settings',
            component: SettingsPage,
            children: [{ path: 'email', component: EmailPanel }],
          },
        ]),
      ],
    });
    const harness = await RouterTestingHarness.create('/settings/email');
    await harness.fixture.whenStable();
    return harness.fixture.nativeElement as HTMLElement;
  }

  it('starts at h2 below the layout h1 and nests the panel title under it', async () => {
    const host = await render();

    expect(
      [...host.querySelectorAll('h1, h2, h3, h4, h5, h6')].map(
        (h) => `${h.tagName.toLowerCase()} ${h.textContent?.trim()}`,
      ),
    ).toEqual(['h2 System settings', 'h3 Email']);
    expect(host.querySelector('main')).toBeNull();
  });

  it('names the narrow-screen panel sheet by the settings title', async () => {
    const host = await render();
    const body = host.ownerDocument.body;

    host.querySelector<HTMLButtonElement>('[data-testid="settings-nav-trigger"]')!.click();
    await vi.waitFor(() => expect(body.querySelector('[role="dialog"]')).not.toBeNull());

    const sheet = body.querySelector('[role="dialog"]')!;
    const title = body.ownerDocument.getElementById(sheet.getAttribute('aria-labelledby')!);
    expect(title?.textContent?.trim()).toBe('System settings');
    body.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  });
});
