import { Injectable, Signal, computed, inject } from '@angular/core';
import { Router, isActive } from '@angular/router';
//#if (IncludeLocalization)
import { translateObjectSignal, translateSignal } from '@jsverse/transloco';
//#endif

import { AuthorizationService } from '../../core/services/authorization-service';
import { LayoutService } from '../../core/services/layout-service';
import { PERMISSIONS } from '../../shared/constants/permission.constants';

//#if (IncludeLocalization)
/** 本地化形态下菜单项的 label 是这个前缀下的词条键。 */
const SIDEBAR_PREFIX = 'layout.sidebar.';

//#endif
export interface MenuItem {
  label: string;
  icon: string;
  route: string;
  /** 所需权限；拥有任意一个即可见。省略表示只要能进入本区就可见。 */
  permissions?: string[];
}

export interface MenuGroup {
  /** 分组标题，必填：没有标题的组就是变相的兜底组（见 docs/standards/frontend-navigation.md「导航与菜单分组」）。 */
  label: string;
  items: MenuItem[];
  /**
   * 顶栏布局里这一组放在哪一侧：左侧是"这个区能做的事"，右侧是"关于我自己的"。
   * 侧栏布局不看这个值，按数组顺序自上而下排。
   */
  placement?: 'start' | 'end';
}

/**
 * 两个区的菜单与区域切换：侧栏（管理平台）与顶栏（工作空间）读同一份信息架构。分组判据只写在
 * docs/standards/frontend-navigation.md「导航与菜单分组」，新增入口先在那里找落位；分组骨架由
 * default-sidebar.spec.ts 钉住。
 */
@Injectable({ providedIn: 'root' })
export class NavigationService {
  private readonly layoutService = inject(LayoutService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly router = inject(Router);
  //#if (IncludeLocalization)
  private readonly workspaceLabel = translateSignal('menu.workspace');
  private readonly platformLabel = translateSignal('menu.platform');
  /** 菜单文案都在 `layout.sidebar` 下：整段取成对象，词条到达与语言切换时菜单随之重算。 */
  private readonly sidebarTexts = translateObjectSignal('layout.sidebar');
  //#endif
  //#if (IncludeLocalization)
  // 存词条键，展示时再翻译：menuGroups 读了活动语言，切换语言时重算，标签随之更新。
  private readonly platformMenuGroups: MenuGroup[] = [
    {
      label: 'layout.sidebar.groupWork',
      items: [{ label: 'layout.sidebar.dashboard', icon: 'lucideGauge', route: '/platform' }],
    },
    // 管理侧的业务菜单加在这里（工作之后、身份与访问之前）：管理员先看业务，再管人和系统。
    {
      label: 'layout.sidebar.groupIdentity',
      items: [
        {
          label: 'layout.sidebar.users',
          icon: 'lucideUsers',
          route: '/platform/users',
          permissions: [PERMISSIONS.users.default],
        },
        {
          label: 'layout.sidebar.roles',
          icon: 'lucideShieldCheck',
          route: '/platform/roles',
          permissions: [PERMISSIONS.roles.default],
        },
        //#if (LocalIdentity && IncludeMultiTenancy)
        // 宿主侧专属：租户用户的 current 权限里不会出现 App.Tenants，按权限自动裁剪。
        {
          label: 'layout.sidebar.tenants',
          icon: 'lucideBuilding2',
          route: '/platform/tenants',
          permissions: [PERMISSIONS.tenants.default],
        },
        //#endif
      ],
    },
    //#if (OpenIddictServer)
    {
      label: 'layout.sidebar.groupDeveloper',
      items: [
        {
          label: 'layout.sidebar.openApplications',
          icon: 'lucideIdCard',
          route: '/platform/open-applications',
          permissions: [PERMISSIONS.openApplications.default],
        },
      ],
    },
    //#endif
    //#if (IncludeOperationRecords)
    // 审计：谁在什么时候做了什么。读者是排查问题和对账的人，不是改配置的人。
    {
      label: 'layout.sidebar.groupAudit',
      items: [
        {
          label: 'layout.sidebar.operationRecords',
          icon: 'lucideDatabase',
          route: '/platform/operation-records',
          permissions: [PERMISSIONS.operationRecords.default],
        },
      ],
    },
    //#endif
    // 系统设置是本租户（或宿主）的默认值与策略，按 App.Settings 裁剪；个人设置在工作空间的「个人」组。
    {
      label: 'layout.sidebar.groupSystem',
      items: [
        {
          label: 'layout.sidebar.systemSettings',
          icon: 'lucideSettings',
          route: '/platform/settings',
          permissions: [PERMISSIONS.settings.default],
        },
      ],
    },
  ];

  private readonly workspaceMenuGroups: MenuGroup[] = [
    {
      label: 'layout.sidebar.groupWork',
      items: [
        { label: 'layout.sidebar.workbench', icon: 'lucideGauge', route: '/workspace/dashboard' },
      ],
    },
    {
      // 模板在这里只放一个示例模块：下游项目把它换成自己的业务入口，
      // 分组本身与判据留着，新入口就不必再重新想一遍该摆哪。
      label: 'layout.sidebar.groupBusiness',
      items: [
        {
          label: 'layout.sidebar.exampleModule',
          icon: 'lucideLayers',
          route: '/workspace/placeholder',
        },
      ],
    },
  ];
  //#else
  private readonly platformMenuGroups: MenuGroup[] = [
    {
      label: 'Work',
      items: [{ label: 'Dashboard', icon: 'lucideGauge', route: '/platform' }],
    },
    // 管理侧的业务菜单加在这里（工作之后、身份与访问之前）：管理员先看业务，再管人和系统。
    {
      label: 'Identity & access',
      items: [
        {
          label: 'User Management',
          icon: 'lucideUsers',
          route: '/platform/users',
          permissions: [PERMISSIONS.users.default],
        },
        {
          label: 'Role Management',
          icon: 'lucideShieldCheck',
          route: '/platform/roles',
          permissions: [PERMISSIONS.roles.default],
        },
        //#if (LocalIdentity && IncludeMultiTenancy)
        // 宿主侧专属：租户用户的 current 权限里不会出现 App.Tenants，按权限自动裁剪。
        {
          label: 'Tenant Management',
          icon: 'lucideBuilding2',
          route: '/platform/tenants',
          permissions: [PERMISSIONS.tenants.default],
        },
        //#endif
      ],
    },
    //#if (OpenIddictServer)
    {
      label: 'Developer',
      items: [
        {
          label: 'Open Applications',
          icon: 'lucideIdCard',
          route: '/platform/open-applications',
          permissions: [PERMISSIONS.openApplications.default],
        },
      ],
    },
    //#endif
    //#if (IncludeOperationRecords)
    // 审计：谁在什么时候做了什么。读者是排查问题和对账的人，不是改配置的人。
    {
      label: 'Audit',
      items: [
        {
          label: 'Operation records',
          icon: 'lucideDatabase',
          route: '/platform/operation-records',
          permissions: [PERMISSIONS.operationRecords.default],
        },
      ],
    },
    //#endif
    // 系统设置是本租户（或宿主）的默认值与策略；个人设置在工作空间的「个人」组。
    {
      label: 'System',
      items: [
        {
          label: 'System settings',
          icon: 'lucideSettings',
          route: '/platform/settings',
          permissions: [PERMISSIONS.settings.default],
        },
      ],
    },
  ];

  private readonly workspaceMenuGroups: MenuGroup[] = [
    {
      label: 'Work',
      items: [{ label: 'Workbench', icon: 'lucideGauge', route: '/workspace/dashboard' }],
    },
    {
      // 模板在这里只放一个示例模块：下游项目把它换成自己的业务入口，
      // 分组本身与判据留着，新入口就不必再重新想一遍该摆哪。
      label: 'Business',
      items: [{ label: 'Example module', icon: 'lucideLayers', route: '/workspace/placeholder' }],
    },
  ];
  //#endif

  /** 可去的区域；只有一个时（无平台权限）品牌块退回普通链接，不给只有当前项的下拉。 */
  readonly areaOptions = computed(() => {
    const isPlatform = this.layoutService.isPlatform();
    const areas = [
      {
        route: '/workspace/dashboard',
        icon: 'lucideHouse',
        //#if (IncludeLocalization)
        label: this.workspaceLabel(),
        //#else
        label: 'Workspace',
        //#endif
        current: !isPlatform,
      },
    ];

    // 进平台要权限；回工作空间无条件——与头像菜单同一口径。
    if (this.authorizationService.canAccessPlatform()) {
      areas.push({
        route: '/platform',
        icon: 'lucideCog',
        //#if (IncludeLocalization)
        label: this.platformLabel(),
        //#else
        label: 'Admin platform',
        //#endif
        current: isPlatform,
      });
    }

    return areas;
  });

  readonly currentAreaLabel = computed(
    () => this.areaOptions().find((area) => area.current)?.label ?? '',
  );

  goToArea(route: string): void {
    void this.router.navigate([route]);
  }

  readonly menuGroups = computed(() => {
    const groups = this.layoutService.isPlatform()
      ? this.platformMenuGroups
      : this.workspaceMenuGroups;
    //#if (IncludeLocalization)
    const texts = this.sidebarTexts();
    const label = (key: string): string => texts[key.slice(SIDEBAR_PREFIX.length)] ?? key;
    //#endif

    return groups
      .map((group) => ({
        ...group,
        //#if (IncludeLocalization)
        label: label(group.label),
        items: group.items
          .filter((item) => this.isItemVisible(item))
          .map((item) => ({ ...item, label: label(item.label) })),
        //#else
        items: group.items.filter((item) => this.isItemVisible(item)),
        //#endif
      }))
      .filter((group) => group.items.length > 0);
  });

  /** 菜单可见性只按权限判断；超级管理员旁路已体现在下发的权限集合中。 */
  private isItemVisible(item: MenuItem): boolean {
    return !item.permissions?.length || this.authorizationService.hasAny(...item.permissions);
  }

  /** 每个菜单路由一个"是否当前"信号，按路由缓存，避免每次渲染新建。 */
  private readonly activeByRoute = new Map<string, Signal<boolean>>();

  /**
   * 菜单项是否为当前页：只比较路径，忽略查询参数与锚点。
   * 列表页的分页、排序、筛选都写在查询参数里，按完整 URL 比较的话一翻页当前项就丢了。
   */
  isItemActive(item: MenuItem): boolean {
    let active = this.activeByRoute.get(item.route);
    if (!active) {
      active = isActive(item.route, this.router, {
        // 平台落地页是其余平台路由的前缀，只能精确匹配；其余入口连同子页面（如设置面板）都算当前
        paths: item.route === '/platform' ? 'exact' : 'subset',
        queryParams: 'ignored',
        fragment: 'ignored',
        matrixParams: 'ignored',
      });
      this.activeByRoute.set(item.route, active);
    }
    return active();
  }
}
