import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
//#if (IncludeLocalization)
import { of } from 'rxjs';
//#endif

import { SystemSettings } from './system-settings';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { SettingOutputDto } from '../../../../core/settings/setting.dto';

function row(name: string, group: string, canManage = true): SettingOutputDto {
  return {
    name,
    displayName: name,
    group,
    groupDisplayName: group,
    userValue: null,
    tenantValue: null,
    defaultValue: null,
    allowsTenantScope: true,
    allowsUserScope: false,
    allowsHostScope: false,
    minimum: null,
    maximum: null,
    canManage,
  };
}

@Component({
  selector: 'app-test-settings-panel',
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Panel {}

/** 面板来自可管理的分组：说明按分组标识取，后端新增而前端没登记的分组照常出现、只是没有说明。 */
describe('SystemSettings panels', () => {
  const description = 'How much detail the logs record';
  let harness: RouterTestingHarness;

  async function render(
    settings: SettingOutputDto[] = [
      row('Logging.MinimumLevel', 'Operations'),
      row('Custom.Probe', 'CustomGroup'),
    ],
    url = '/settings',
  ): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          {
            path: 'settings',
            component: SystemSettings,
            children: [{ path: ':group', component: Panel }],
          },
        ]),
        { provide: AuthService, useValue: { clearAuthData: () => undefined } },
        //#if (IncludeLocalization)
        // 设置页的词条在 settings 作用域里；只给本用例要断言的那一条
        ...provideTranslocoTesting(['en'], {
          getTranslation: (lang: string) =>
            of(lang === 'settings/en' ? { groupDescriptions: { Operations: description } } : {}),
        }),
        //#endif
      ],
    });
    harness = await RouterTestingHarness.create(url);
    TestBed.inject(HttpTestingController).expectOne('/api/v1/settings').flush(settings);
    await harness.fixture.whenStable();
    return harness.fixture.nativeElement as HTMLElement;
  }

  const panelDescription = (host: HTMLElement) =>
    host.querySelector('h3')?.parentElement?.querySelector('p')?.textContent?.trim() ?? null;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('describes a known group and leaves an unknown group without a description', async () => {
    const host = await render();

    // 没带面板时落到第一个面板
    await vi.waitFor(() =>
      expect(host.querySelector('h3')?.textContent?.trim()).toBe('Operations'),
    );
    expect(panelDescription(host)).toBe(description);

    await harness.navigateByUrl('/settings/custom-group');
    expect(host.querySelector('h3')?.textContent?.trim()).toBe('CustomGroup');
    expect(panelDescription(host)).toBeNull();
  });

  // 只有系统设置入口、没有任何分组的管理子权限：面板导航为空，给出说明而不是一片空白
  it('explains that nothing can be managed when no group is manageable', async () => {
    const host = await render([row('Security.RequireTwoFactor', 'Security', false)]);

    await vi.waitFor(() =>
      expect(host.querySelector('[data-testid="settings-empty"]')).not.toBeNull(),
    );
    expect(host.querySelector('h3')).toBeNull();
  });

  // 收藏的面板地址：回到不带面板的地址，只留本页这一份空态，不再激活面板
  it('leaves a bookmarked panel when no group is manageable', async () => {
    const host = await render(
      [row('Security.RequireTwoFactor', 'Security', false)],
      '/settings/operations',
    );

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/settings'));
    expect(host.querySelectorAll('[data-testid="settings-empty"]')).toHaveLength(1);
  });

  it('leaves out groups the user cannot manage', async () => {
    const host = await render([
      row('Security.RequireTwoFactor', 'Security', false),
      row('Logging.MinimumLevel', 'Operations'),
    ]);

    await vi.waitFor(() =>
      expect(host.querySelector('h3')?.textContent?.trim()).toBe('Operations'),
    );
    expect(host.textContent).not.toContain('Security');
    expect(host.querySelector('[data-testid="settings-empty"]')).toBeNull();
  });
});
