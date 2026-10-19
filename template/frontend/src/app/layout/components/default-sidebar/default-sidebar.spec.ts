import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { Translation, TranslocoService } from '@jsverse/transloco';
//#endif
import { HlmSidebarImports, HlmSidebarService } from '@spartan-ng/helm/sidebar';
//#if (IncludeLocalization)
import { Subject } from 'rxjs';
//#endif

import { DefaultSidebar } from './default-sidebar';
//#if (IncludeLocalization)
import enTexts from '../../../../../public/i18n/en.json';
//#endif
//#if (IncludeLocalization && IncludeRealTime)
import zhCnTexts from '../../../../../public/i18n/zh-CN.json';
//#endif
//#if (IncludeLocalization)
import { lineCount, pseudoCatalog } from '../../../core/i18n/pseudo-locale.testing';
import { provideTranslocoTesting } from '../../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../../core/services/authorization-service';
import { LayoutService } from '../../../core/services/layout-service';
//#if (IncludeRealTime)
import {
  type RealtimeConnectionStatus,
  SignalRService,
} from '../../../core/services/signalr-service';
//#endif
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

//#if (IncludeLocalization)
/**
 * 伪语言按标签容量放宽文案（frontend-i18n.md §5）：展开的侧栏里分组标题与菜单项仍完整放下；
 * 品牌块的应用名随项目改名、长度不定，放不下时单行截断，不撑出侧栏。
 */
describe('DefaultSidebar pseudo locale', () => {
  for (const platform of [true, false]) {
    it(`fits the ${platform ? 'platform' : 'workspace'} menu into the expanded sidebar`, async () => {
      TestBed.configureTestingModule({
        // prettier-ignore
        providers: [
          provideRouter([]),
          provideHttpClient(),
          provideHttpClientTesting(),
          {
            provide: LayoutService,
            useValue: {
              isPlatform: signal(platform),
              currentUrl: signal(platform ? '/platform' : '/workspace/dashboard'),
              homeRoute: signal('/workspace/dashboard'),
            },
          },
          {
            provide: AuthorizationService,
            useValue: { hasAny: () => true, canAccessPlatform: () => true },
          },
          ...provideTranslocoTesting(),
        ],
      });
      TestBed.inject(TranslocoService).setTranslation(pseudoCatalog(enTexts), 'en');
      const fixture = TestBed.createComponent(SidebarHost);
      TestBed.inject(HlmSidebarService).setOpen(true);
      fixture.autoDetectChanges();
      await fixture.whenStable();
      await Promise.all(document.getAnimations().map((animation) => animation.finished));
      const host = fixture.nativeElement as HTMLElement;

      const sidebar = Array.from(
        host.querySelectorAll<HTMLElement>('[data-sidebar="sidebar"]'),
      ).find((panel) => panel.getBoundingClientRect().width > 0)!;
      const labels = Array.from(
        sidebar.querySelectorAll<HTMLElement>(
          '[data-sidebar="group-label"], [data-slot="sidebar-menu-button"] > span:last-child',
        ),
      );
      expect(labels.length).toBeGreaterThan(0);
      for (const label of labels) {
        const text = label.textContent!.trim();
        // 文案来自词条：伪语言的方括号在，写死的文案没有
        expect(text).toMatch(/^\[.+\]$/);
        expect(label.getBoundingClientRect().width, text).toBeGreaterThan(0);
        // 完整放下：没被截断，也没折成两行
        expect(label.scrollWidth, text).toBeLessThanOrEqual(label.clientWidth);
        expect(lineCount(label), text).toBe(1);
      }

      // 品牌块两行：应用名与当前区域，都可见、各占一行
      const brand = Array.from(
        sidebar.querySelectorAll<HTMLElement>(
          '[data-slot="sidebar-header"] [data-slot="sidebar-menu-button"] .grid > span',
        ),
      );
      expect(brand).toHaveLength(2);
      for (const line of brand) {
        const box = line.getBoundingClientRect();
        expect(box.width > 0 && box.height > 0, line.textContent!).toBe(true);
        expect(line.textContent!.trim()).toMatch(/^\[.+\]$/);
        expect(lineCount(line), line.textContent!).toBe(1);
      }
      expect(sidebar.scrollWidth).toBeLessThanOrEqual(sidebar.clientWidth);
    });
  }
});
//#endif

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
//#if (IncludeRealTime)

/**
 * 侧栏底部的实时状态是标签，按最长语言也要一行放下：可见的是短状态，完整说明在可聚焦的提示与读屏文本里。
 * 渲染用的是正式词条（多语言形态为 `public/i18n` 的全局词条，否则为组件的英文表），期望文案在这里逐字写出、
 * 不从词条推导：正式词条改长、模板改回整句都会让这组用例变红。
 */
describe('DefaultSidebar real-time status', () => {
  interface Expected {
    readonly status: RealtimeConnectionStatus;
    readonly short: string;
    readonly full: string;
  }

  const ENGLISH_STATES: readonly Expected[] = [
    { status: 'connected', short: 'Connected', full: 'Live updates connected' },
    { status: 'reconnecting', short: 'Reconnecting…', full: 'Connection lost, reconnecting…' },
    { status: 'disconnected', short: 'Offline', full: 'Live updates offline. Refresh to retry.' },
  ];
  //#if (IncludeLocalization)
  const CHINESE_STATES: readonly Expected[] = [
    { status: 'connected', short: '已连接', full: '实时更新已连接' },
    { status: 'reconnecting', short: '正在重连…', full: '连接中断，正在重连…' },
    { status: 'disconnected', short: '已断开', full: '实时更新已断开，刷新页面重试。' },
  ];
  //#endif

  const status = signal<RealtimeConnectionStatus>('idle');

  beforeEach(() => {
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SignalRService, useValue: { connectionStatus: status } },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en', 'zh-CN']),
        //#endif
      ],
    });
    //#if (IncludeLocalization)
    const transloco = TestBed.inject(TranslocoService);
    transloco.setTranslation(enTexts, 'en');
    transloco.setTranslation(zhCnTexts, 'zh-CN');
    //#endif
    onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());
  });

  async function render(expected: Expected): Promise<HTMLElement> {
    status.set(expected.status);
    const fixture = TestBed.createComponent(SidebarHost);
    // 折叠状态会跨用例留在浏览器里（上面的触屏用例把侧栏收成了图标轨），这里按展开的侧栏宽度量
    TestBed.inject(HlmSidebarService).setOpen(true);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[data-testid="realtime-status"]',
    )!;
  }

  async function expectOneLineStatus(expected: Expected): Promise<void> {
    const box = await render(expected);
    const text = box.querySelector<HTMLElement>('[data-testid="realtime-status-text"]')!;
    const badge = box.querySelector<HTMLElement>('[hlmBadge]')!;
    // 量的是真实渲染的状态框：收起或隐藏时下面的尺寸断言全都会空过
    expect(text.getBoundingClientRect().width, expected.status).toBeGreaterThan(0);

    expect(text.textContent!.trim(), expected.status).toBe(expected.short);
    // 短词完整可见：没有被 truncate 截成省略号
    expect(text.scrollWidth, expected.status).toBeLessThanOrEqual(text.clientWidth);
    // 一行：状态框只比徽章高出内边距与边框
    const lineHeight = Number.parseFloat(getComputedStyle(text).lineHeight);
    expect(text.getBoundingClientRect().height, expected.status).toBeLessThan(lineHeight * 1.5);
    expect(
      box.getBoundingClientRect().height - badge.getBoundingClientRect().height,
      expected.status,
    ).toBeLessThanOrEqual(18.5);
    // 常驻状态区里给读屏的完整说明，不放在可聚焦元素里
    const full = box.querySelector<HTMLElement>('.sr-only')!;
    expect(full.textContent!.trim(), expected.status).toBe(expected.full);
    expect(text.contains(full)).toBe(false);
  }

  for (const expected of ENGLISH_STATES) {
    it(`keeps the ${expected.status} status on one line in English`, async () => {
      await expectOneLineStatus(expected);
    });
  }
  //#if (IncludeLocalization)

  for (const expected of CHINESE_STATES) {
    it(`keeps the ${expected.status} status on one line in Chinese`, async () => {
      TestBed.inject(TranslocoService).setActiveLang('zh-CN');
      await expectOneLineStatus(expected);
    });
  }
  //#endif

  // 聚焦时读到短状态，提示作为描述给出完整说明：名称与描述不是同一句，不会读两遍
  it('describes the short status with the full sentence when focused', async () => {
    const box = await render(ENGLISH_STATES[2]);
    const text = box.querySelector<HTMLElement>('[data-testid="realtime-status-text"]')!;

    // 聚焦引起的页面滚动会让提示立即收起，这里只验证聚焦本身
    text.focus({ preventScroll: true });
    await expect.poll(() => text.getAttribute('aria-describedby')).not.toBeNull();
    const description = document.getElementById(text.getAttribute('aria-describedby')!)!;
    expect(description.textContent!.trim()).toBe('Live updates offline. Refresh to retry.');
    expect(text.textContent!.trim()).toBe('Offline');
  });
});
//#endif
