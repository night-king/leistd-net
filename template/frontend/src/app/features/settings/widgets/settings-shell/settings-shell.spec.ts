import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { page } from 'vitest/browser';

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
    { path: 'email', label: 'Email', icon: 'lucideChevronDown', description: 'Outgoing mail.' },
    { path: 'audit', label: 'Audit', icon: 'lucideChevronDown' },
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
describe('SettingsShell', () => {
  let harness: RouterTestingHarness;

  async function render(): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: 'settings',
            component: SettingsPage,
            children: [
              { path: 'email', component: EmailPanel },
              { path: 'audit', component: EmailPanel },
            ],
          },
        ]),
      ],
    });
    harness = await RouterTestingHarness.create('/settings/email');
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

  // 可见文字是当前面板名，可访问名必须包含它（WCAG 2.5.3），所以不另挂 aria-label；切换面板后随之更新
  it('names the narrow-screen trigger by the current panel and follows navigation', async () => {
    const host = await render();
    const trigger = host.querySelector<HTMLButtonElement>('[data-testid="settings-nav-trigger"]')!;

    expect(trigger.hasAttribute('aria-label')).toBe(false);
    expect(trigger.textContent?.replace(/\s+/g, ' ').trim()).toBe('Email, Settings panels');

    await harness.navigateByUrl('/settings/audit');
    expect(trigger.textContent?.replace(/\s+/g, ' ').trim()).toBe('Audit, Settings panels');
  });

  it('returns focus to the trigger when the panel sheet closes', async () => {
    // 触发器只在窄屏显示，宽屏下它是隐藏的、拿不到焦点
    await page.viewport(390, 844);
    onTestFinished(() => page.viewport(1280, 800));
    const host = await render();
    const body = host.ownerDocument.body;
    const trigger = host.querySelector<HTMLButtonElement>('[data-testid="settings-nav-trigger"]')!;

    trigger.focus();
    trigger.click();
    await vi.waitFor(() =>
      expect(
        body.querySelector('[role="dialog"]')?.contains(host.ownerDocument.activeElement),
      ).toBe(true),
    );
    host.ownerDocument.activeElement!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }),
    );

    await vi.waitFor(
      () => {
        expect(body.querySelector('[role="dialog"]')).toBeNull();
        expect(host.ownerDocument.activeElement).toBe(trigger);
      },
      { timeout: 3000 },
    );
    body.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  });

  it('switches panels from the sheet and closes it', async () => {
    await page.viewport(390, 844);
    onTestFinished(() => page.viewport(1280, 800));
    const host = await render();
    const body = host.ownerDocument.body;
    const trigger = host.querySelector<HTMLButtonElement>('[data-testid="settings-nav-trigger"]')!;

    trigger.click();
    await vi.waitFor(() => expect(body.querySelector('[role="dialog"]')).not.toBeNull());
    const audit = [...body.querySelectorAll<HTMLAnchorElement>('[role="dialog"] nav a')].find(
      (link) => link.textContent?.trim() === 'Audit',
    )!;
    audit.click();

    await vi.waitFor(
      () => {
        expect(body.querySelector('[role="dialog"]')).toBeNull();
        expect(trigger.textContent?.replace(/\s+/g, ' ').trim()).toBe('Audit, Settings panels');
      },
      { timeout: 3000 },
    );
    expect(TestBed.inject(Router).url).toBe('/settings/audit');
    body.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  });
});
