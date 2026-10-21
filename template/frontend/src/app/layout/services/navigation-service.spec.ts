import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { NavigationService } from './navigation-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../core/services/authorization-service';

@Component({ template: '' })
class Page {}

/**
 * 当前菜单项的判定。列表页的分页、排序、筛选都写在查询参数里，
 * 按完整 URL 比较的话，一翻页侧栏和顶栏的当前项就会丢失。
 */
describe('NavigationService current item', () => {
  let service: NavigationService;
  let router: Router;

  const item = (route: string) => ({ label: route, icon: 'lucideGauge', route });

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: 'platform',
            children: [
              { path: '', component: Page },
              { path: 'users', component: Page },
              { path: 'settings/:panel', component: Page },
            ],
          },
        ]),
        {
          provide: AuthorizationService,
          useValue: { hasAny: () => true, canAccessPlatform: signal(true) },
        },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(),
        //#endif
      ],
    });
    service = TestBed.inject(NavigationService);
    router = TestBed.inject(Router);
  });

  it('keeps a list page current while its query parameters change', async () => {
    const users = item('/platform/users');
    await router.navigateByUrl('/platform/users');
    expect(service.isItemActive(users)).toBe(true);

    // 翻页、筛选只改查询参数与锚点
    await router.navigateByUrl('/platform/users?page=2&isActive=true#top');
    expect(service.isItemActive(users)).toBe(true);
    expect(service.isItemActive(item('/platform'))).toBe(false);

    await router.navigateByUrl('/platform/settings/operations');
    expect(service.isItemActive(users)).toBe(false);
  });

  it('treats sub-pages as part of their menu entry', async () => {
    await router.navigateByUrl('/platform/settings/operations');

    expect(service.isItemActive(item('/platform/settings'))).toBe(true);
  });

  it('matches the platform landing page only exactly', async () => {
    await router.navigateByUrl('/platform');
    expect(service.isItemActive(item('/platform'))).toBe(true);

    await router.navigateByUrl('/platform/users');
    expect(service.isItemActive(item('/platform'))).toBe(false);
  });
});
