import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
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

function row(name: string, group: string): SettingOutputDto {
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
  };
}

@Component({
  selector: 'app-test-settings-panel',
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Panel {}

/** 面板说明按分组标识取：登记过的分组有说明，后端新增而前端没登记的分组照常出现、只是没有说明。 */
describe('SystemSettings panel descriptions', () => {
  const description = 'How much detail the logs record';
  let harness: RouterTestingHarness;

  async function render(): Promise<HTMLElement> {
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
    harness = await RouterTestingHarness.create('/settings');
    TestBed.inject(HttpTestingController)
      .expectOne('/api/v1/settings')
      .flush([row('Logging.MinimumLevel', 'Operations'), row('Custom.Probe', 'CustomGroup')]);
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
});
