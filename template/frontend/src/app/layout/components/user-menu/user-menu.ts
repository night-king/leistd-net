import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Router } from '@angular/router';
//#if (IncludeLocalization)
import { translateSignal } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideChevronsUpDown,
  lucideCog,
  lucideHouse,
  lucideLogOut,
  lucideUserCog,
} from '@ng-icons/lucide';
import { HlmAvatarImports } from '@spartan-ng/helm/avatar';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmSidebarImports, HlmSidebarService } from '@spartan-ng/helm/sidebar';

import { AuthService } from '../../../core/services/auth-service';
import { AuthorizationService } from '../../../core/services/authorization-service';
import { LayoutService } from '../../../core/services/layout-service';

/** 用户菜单项：普通项（label + lucide 图标 + 动作）或分隔线。 */
interface UserMenuItem {
  label?: string;
  icon?: string;
  action?: () => void;
  separator?: boolean;
}

/**
 * 用户菜单，两种形态共用同一份下拉内容（区域切换、个人设置、退出；构成由 user-menu.spec.ts 钉住）：
 * - `sidebar`：侧栏底部的 nav-user 写法。桌面向右、手机向上弹出，图标栏下向上弹会盖住图标；
 *   单独关掉 `closeMobileSidebarOnClick`，否则全局的点击即关抽屉会连锚点一起关掉；
 * - `topbar`：顶栏右侧的头像下拉。
 * 退出不用 `destructive`：红色只留给危险、删除与错误。
 */
@Component({
  selector: 'app-user-menu',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  // 顶栏里宿主须是 flex：块级宿主里只有图片的按钮落在基线上，下伸空间会把头像撑高约 6px。
  // 侧栏形态保持块级。
  host: { '[class.inline-flex]': "variant() === 'topbar'" },
  imports: [
    NgIcon,
    ...HlmAvatarImports,
    HlmButton,
    ...HlmDropdownMenuImports,
    ...HlmSidebarImports,
  ],
  // prettier-ignore
  providers: [
    provideIcons({
      lucideChevronsUpDown,
      lucideHouse,
      lucideCog,
      lucideUserCog,
      lucideLogOut,
    }),
  ],
  template: `
    @if (authService.currentUser(); as user) {
      @if (variant() === 'topbar') {
        <!-- 顶栏：只放头像，姓名与邮箱在展开后的菜单头部 -->
        <button
          hlmBtn
          variant="ghost"
          size="icon"
          class="rounded-full"
          data-testid="user-menu-trigger"
          [hlmDropdownMenuTrigger]="userMenu"
          align="end"
          [attr.aria-label]="user.displayName || user.username"
        >
          <hlm-avatar>
            <img
              [src]="
                user.avatar || 'https://api.dicebear.com/7.x/avataaars/svg?seed=' + user.username
              "
              hlmAvatarImage
              [alt]="user.username"
            />
            <span hlmAvatarFallback>{{ (user.displayName || user.username)[0] }}</span>
          </hlm-avatar>
        </button>
      } @else {
        <ul hlmSidebarMenu>
          <li hlmSidebarMenuItem>
            <button
              hlmSidebarMenuButton
              size="lg"
              [closeMobileSidebarOnClick]="false"
              [hlmDropdownMenuTrigger]="userMenu"
              [side]="sidebarMenuSide()"
              align="end"
              class="data-[state=open]:bg-sidebar-accent data-[state=open]:text-sidebar-accent-foreground"
            >
              <hlm-avatar class="rounded-md">
                <img
                  [src]="
                    user.avatar ||
                    'https://api.dicebear.com/7.x/avataaars/svg?seed=' + user.username
                  "
                  hlmAvatarImage
                  [alt]="user.username"
                />
                <span hlmAvatarFallback class="rounded-md">{{
                  (user.displayName || user.username)[0]
                }}</span>
              </hlm-avatar>
              <div class="grid flex-1 text-left text-sm leading-tight">
                <span class="truncate font-medium">{{ user.displayName || user.username }}</span>
                <span class="truncate text-xs text-muted-foreground">{{ user.email }}</span>
              </div>
              <ng-icon name="lucideChevronsUpDown" class="ml-auto text-base" />
            </button>
          </li>
        </ul>
      }

      <ng-template #userMenu>
        <hlm-dropdown-menu class="min-w-56">
          <hlm-dropdown-menu-label>
            <div class="flex items-center gap-2 px-1 py-1.5 text-left text-sm">
              <hlm-avatar [class.rounded-md]="variant() === 'sidebar'">
                <img
                  [src]="
                    user.avatar ||
                    'https://api.dicebear.com/7.x/avataaars/svg?seed=' + user.username
                  "
                  hlmAvatarImage
                  [alt]="user.username"
                />
                <span hlmAvatarFallback [class.rounded-md]="variant() === 'sidebar'">{{
                  (user.displayName || user.username)[0]
                }}</span>
              </hlm-avatar>
              <div class="grid flex-1 text-left text-sm leading-tight">
                <span class="truncate font-medium">{{ user.displayName || user.username }}</span>
                <span class="truncate text-xs text-muted-foreground">{{ user.email }}</span>
              </div>
            </div>
          </hlm-dropdown-menu-label>
          <hlm-dropdown-menu-separator />
          @for (group of menuGroups(); track $index) {
            @if (!$first) {
              <hlm-dropdown-menu-separator />
            }
            <hlm-dropdown-menu-group>
              @for (item of group; track item.label) {
                <button hlmDropdownMenuItem (click)="item.action!()">
                  <ng-icon [name]="item.icon!" />
                  {{ item.label }}
                </button>
              }
            </hlm-dropdown-menu-group>
          }
        </hlm-dropdown-menu>
      </ng-template>
    }
  `,
})
export class UserMenu {
  /** 放在哪种布局里：侧栏底部（带姓名与邮箱）或顶栏右侧（只有头像）。菜单内容两者相同。 */
  readonly variant = input<'sidebar' | 'topbar'>('sidebar');

  readonly authService = inject(AuthService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly router = inject(Router);
  private readonly layoutService = inject(LayoutService);
  //#if (IncludeLocalization)
  private readonly texts = {
    workspace: translateSignal('menu.workspace'),
    platform: translateSignal('menu.platform'),
    personalSettings: translateSignal('menu.personalSettings'),
    logout: translateSignal('menu.logout'),
  };
  //#endif
  private readonly sidebarService = inject(HlmSidebarService);

  /** 侧栏形态的弹出方向，同官方 nav-user；顶栏形态沿用默认（向下）。 */
  readonly sidebarMenuSide = computed(() => (this.sidebarService.isMobile() ? 'top' : 'right'));

  readonly userMenuItems = computed<UserMenuItem[]>(() => {
    const items: UserMenuItem[] = [];

    if (this.layoutService.isPlatform()) {
      items.push({
        //#if (IncludeLocalization)
        label: this.texts.workspace(),
        //#else
        label: 'Workspace',
        //#endif
        icon: 'lucideHouse',
        action: () => this.router.navigate(['/workspace']),
      });
    } else if (
      this.layoutService.currentUrl().startsWith('/workspace') &&
      this.authorizationService.canAccessPlatform()
    ) {
      items.push({
        //#if (IncludeLocalization)
        label: this.texts.platform(),
        //#else
        label: 'Admin platform',
        //#endif
        icon: 'lucideCog',
        action: () => this.router.navigate(['/platform']),
      });
    }

    if (items.length > 0) {
      items.push({ separator: true });
    }

    items.push(
      // 个人设置只有一处（工作空间），管理平台不另放；不带 LocalIdentity 守卫：没有本地身份时仍有偏好面板。
      {
        //#if (IncludeLocalization)
        label: this.texts.personalSettings(),
        //#else
        label: 'Personal settings',
        //#endif
        icon: 'lucideUserCog',
        action: () => this.router.navigate(['/workspace/settings']),
      },
      { separator: true },
      // 刻意没有「切换租户」：会话租户由 cookie claim 定案，换租户只能退出后在登录页重新选择。
      {
        //#if (IncludeLocalization)
        label: this.texts.logout(),
        //#else
        label: 'Sign out',
        //#endif
        icon: 'lucideLogOut',
        action: () => this.handleLogout(),
      },
    );
    return items;
  });

  /** 按分隔项切成分组，渲染为官方写法的 `hlm-dropdown-menu-group`（带分组语义）。 */
  readonly menuGroups = computed(() =>
    this.userMenuItems()
      .reduce<UserMenuItem[][]>(
        (groups, item) => {
          if (item.separator) {
            groups.push([]);
          } else {
            groups[groups.length - 1].push(item);
          }
          return groups;
        },
        [[]],
      )
      .filter((group) => group.length > 0),
  );

  // 刻意不显示"当前租户"：会话内租户不变，子域名部署下地址栏已是答案。也不能取 TenantContextService：
  // 那是登录入口的路由提示（localStorage），不是会话事实；确需显示时应由会话（WhoAmI）提供展示名。

  handleLogout(): void {
    this.authService.logout();
  }
}
