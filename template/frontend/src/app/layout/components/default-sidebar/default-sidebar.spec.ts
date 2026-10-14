import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { Translation } from '@jsverse/transloco';
//#endif
import { HlmSidebarImports, HlmSidebarService } from '@spartan-ng/helm/sidebar';
//#if (IncludeLocalization)
import { Subject } from 'rxjs';
//#endif

import { DefaultSidebar } from './default-sidebar';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../../core/services/authorization-service';
import { LayoutService } from '../../../core/services/layout-service';
import {
  PERMISSIONS,
  PLATFORM_ENTRY_PERMISSIONS,
} from '../../../shared/constants/permission.constants';
import { expectTouchTarget, setCoarsePointer } from '../../../shared/utils/coarse-pointer.testing';

//#if (IncludeLocalization)
// 空词条下 translate() 回落成键名，所以这里按**键**断言：分组骨架与文案无关，
// 改一句中文不该让这组用例变红。
const WORK = 'layout.sidebar.groupWork';
const BUSINESS = 'layout.sidebar.groupBusiness';
const IDENTITY = 'layout.sidebar.groupIdentity';
//#if (OpenIddictServer)
const DEVELOPER = 'layout.sidebar.groupDeveloper';
//#endif
//#if (IncludeOperationRecords)
const AUDIT = 'layout.sidebar.groupAudit';
//#endif
const SYSTEM = 'layout.sidebar.groupSystem';
//#else
const WORK = 'Work';
const BUSINESS = 'Business';
const IDENTITY = 'Identity & access';
//#if (OpenIddictServer)
const DEVELOPER = 'Developer';
//#endif
//#if (IncludeOperationRecords)
const AUDIT = 'Audit';
//#endif
const SYSTEM = 'System';
//#endif

/**
 * 侧栏菜单的信息架构：钉住两个区的分组骨架、不留兜底组、个人设置只在工作空间、
 * 权限不足的组整组消失。
 */
describe('DefaultSidebar menu groups', () => {
  /** 只构造类，不渲染模板：分组是类上的 computed，模板依赖与信息架构无关的 sidebar 上下文。 */
  function build(options: { platform: boolean; permissions?: readonly string[] }): DefaultSidebar {
    const granted = new Set(options.permissions ?? []);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        // 真实 LayoutService 的 isPlatform 由路由派生，要靠导航才能切换；
        // 本组用例要的只是"当前在哪个区"这一个输入。
        {
          provide: LayoutService,
          useValue: {
            isPlatform: signal(options.platform),
            currentUrl: signal(options.platform ? '/platform' : '/workspace/dashboard'),
          },
        },
        {
          provide: AuthorizationService,
          useValue: {
            hasAny: (...permissions: string[]) => permissions.some((p) => granted.has(p)),
            canAccessPlatform: () => options.platform,
          },
        },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(),
        //#endif
      ],
    });

    return TestBed.runInInjectionContext(() => new DefaultSidebar());
  }

  const groupsOf = (sidebar: DefaultSidebar) => sidebar.menuGroups().map((group) => group.label);
  const routesOf = (sidebar: DefaultSidebar) =>
    sidebar.menuGroups().flatMap((group) => group.items.map((item) => item.route));

  it('orders workspace groups as work → business with no fallback group', () => {
    const sidebar = build({ platform: false });

    expect(groupsOf(sidebar)).toEqual([WORK, BUSINESS]);
    expect(routesOf(sidebar)).toEqual(['/workspace/dashboard', '/workspace/placeholder']);
  });

  // 个人设置只有头像菜单一个入口；工作空间侧由上一条的精确 toEqual 覆盖，这里只查平台侧。
  it('leaves the personal settings entry out of the menu tree', () => {
    const everything = Object.values(PERMISSIONS).map((group) => group.default);

    expect(routesOf(build({ platform: true, permissions: everything }))).not.toContain(
      '/workspace/settings',
    );
  });

  it('orders platform groups work → identity & access → developer → audit → system', () => {
    const permissions = [
      PERMISSIONS.users.default,
      PERMISSIONS.roles.default,
      PERMISSIONS.settings.default,
      //#if (IncludeOperationRecords)
      PERMISSIONS.operationRecords.default,
      //#endif
      //#if (LocalIdentity && IncludeMultiTenancy)
      PERMISSIONS.tenants.default,
      //#endif
      //#if (OpenIddictServer)
      PERMISSIONS.openApplications.default,
      //#endif
    ];
    const expected = [WORK, IDENTITY];
    //#if (OpenIddictServer)
    expected.push(DEVELOPER);
    //#endif
    //#if (IncludeOperationRecords)
    expected.push(AUDIT);
    //#endif
    expected.push(SYSTEM);

    const sidebar = build({ platform: true, permissions });

    expect(groupsOf(sidebar)).toEqual(expected);
    expect(routesOf(sidebar)).toContain('/platform/settings');
  });

  // 能看到任一平台菜单项的账号都必须进得了 /platform，反之入口权限也不能放进一个看不到任何菜单的空平台：
  // 少一项，那个岗位的账号菜单为空、还会被重定向走；多一项，进来只看到工作组。
  it('matches platform entry permissions one-to-one with platform menu items', () => {
    const everything = Object.values(PERMISSIONS).map((group) => group.default);
    const allRoutes = routesOf(build({ platform: true, permissions: everything }));
    TestBed.resetTestingModule();

    expect(routesOf(build({ platform: true, permissions: PLATFORM_ENTRY_PERMISSIONS }))).toEqual(
      allRoutes,
    );
    // 仪表盘随平台常驻，它的权限只决定统计是否可见；其余入口权限各自带出一个菜单项
    for (const permission of PLATFORM_ENTRY_PERMISSIONS) {
      TestBed.resetTestingModule();
      expect(
        routesOf(build({ platform: true, permissions: [permission] })).length,
        permission,
      ).toBeGreaterThan(permission === PERMISSIONS.dashboard.default ? 0 : 1);
    }
  });

  // 权限不足时整组消失，不留一个空标题——空标题看起来像加载失败。
  it('shows only the work group on the platform without admin permissions', () => {
    const sidebar = build({ platform: true });

    expect(groupsOf(sidebar)).toEqual([WORK]);
    expect(routesOf(sidebar)).toEqual(['/platform']);
  });
});
//#if (IncludeLocalization)

/**
 * 菜单文案随词条到达更新：首次读菜单时词条可能还在路上，靠同步 translate() 会把键名
 * 留在菜单上。
 */
describe('DefaultSidebar menu labels', () => {
  it('shows the translated labels once translations arrive after the first read', () => {
    const translations = new Subject<Translation>();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: LayoutService,
          useValue: { isPlatform: signal(false), currentUrl: signal('/workspace/dashboard') },
        },
        {
          provide: AuthorizationService,
          useValue: { hasAny: () => true, canAccessPlatform: () => false },
        },
        ...provideTranslocoTesting(['en'], { getTranslation: () => translations }),
      ],
    });
    const sidebar = TestBed.runInInjectionContext(() => new DefaultSidebar());
    const labels = () => sidebar.menuGroups().map((group) => group.label);
    expect(labels()).not.toContain('Work');

    translations.next({ layout: { sidebar: { groupWork: 'Work', groupBusiness: 'Business' } } });
    translations.complete();

    expect(labels()).toEqual(['Work', 'Business']);
  });
});
//#endif

/** 与默认布局同样的包法：侧栏放在 hlmSidebarWrapper 里，宽度变量与折叠状态都从这里来。 */
@Component({
  selector: 'app-test-sidebar-host',
  imports: [DefaultSidebar, ...HlmSidebarImports],
  template: `<div hlmSidebarWrapper class="h-svh"><app-default-sidebar /></div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class SidebarHost {}

/** 折叠成图标轨时按钮原本只有 32px 见方；触屏下侧栏加宽，按钮宽高都到 44px 且整块可点。 */
describe('DefaultSidebar touch targets', () => {
  afterEach(() => setCoarsePointer(false));

  it('keeps every collapsed menu button a full 44px target on touch screens', async () => {
    await setCoarsePointer(true);
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(),
        //#endif
      ],
    });
    const fixture = TestBed.createComponent(SidebarHost);
    TestBed.inject(HlmSidebarService).setOpen(false);
    fixture.detectChanges();
    await fixture.whenStable();
    // 折叠是宽度过渡，量尺寸要等过渡结束
    await Promise.all(document.getAnimations().map((animation) => animation.finished));

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-collapsible="icon"]')).not.toBeNull();
    const buttons = host.querySelectorAll('[data-slot="sidebar-menu-button"]');
    expect(buttons.length).toBeGreaterThan(0);
    buttons.forEach(expectTouchTarget);
    // 只露图标：按钮放大后，没收起的文字会从图标旁边露出来
    const labels = Array.from(host.querySelectorAll('[data-slot="sidebar-menu-button"] > span'));
    expect(labels.filter((label) => label.getBoundingClientRect().width > 0)).toEqual([]);
    // 搜索文字折叠后整个隐藏，按钮的名称由 aria-label 提供（提示框只是描述，图标对读屏隐藏）
    const search = host.querySelector('[data-testid="sidebar-search"]')!;
    const searchLabel = search.querySelector('span')!.textContent!.trim();
    expect(searchLabel).not.toBe('');
    expect(search.getAttribute('aria-label')).toBe(searchLabel);
  });
});
