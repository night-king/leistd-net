// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  createEnvironmentInjector,
  //#if (IncludeLocalization)
  effect,
  //#endif
  EnvironmentInjector,
  inject,
  NO_ERRORS_SCHEMA,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
//#if (IncludeLocalization)
import { translateSignal, TranslocoService } from '@jsverse/transloco';
//#endif

import { DefaultHeader } from './default-header';
//#if (IncludeLocalization)
import { LanguageSwitcher } from '../../../core/components/language-switcher/language-switcher';
//#endif
import { ThemeModeToggle } from '../../../core/components/theme-mode-toggle/theme-mode-toggle';
import { ConfirmService } from '../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../../core/services/authorization-service';
//#if (Impersonation)
import { ImpersonationService } from '../../../core/services/impersonation-service';
//#endif
import { LayoutService } from '../../../core/services/layout-service';
import { DefaultLayout } from '../../default/default-layout';
import { WorkspaceLayout } from '../../workspace/workspace-layout';
import { DefaultSidebar } from '../default-sidebar/default-sidebar';
//#if (IncludeNotifications)
import { Notifications } from '../notifications/notifications';
//#endif
import { UserMenu } from '../user-menu/user-menu';

/**
 * 页面标题只在带页头的布局里设置；离开布局（去登录页、落地页）时页头负责清掉，
 * 否则浏览器标签页会一直挂着上一个布局页的标题。
 */
describe('DefaultHeader page title', () => {
  it('clears the page title when the header leaves with its layout', () => {
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        provideRouter([]),
        //#if (Impersonation)
        // 模拟退出提示与本用例无关，真实实例会读会话存储
        {
          provide: ImpersonationService,
          useValue: { notifyAfterRenderIfJustExited: () => undefined },
        },
        //#endif
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(),
        //#endif
      ],
    });
    const layout = TestBed.inject(LayoutService);
    // 只构造类、不渲染模板：页头随布局销毁时，它所在的注入上下文随之销毁
    const scope = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    scope.runInContext(() => new DefaultHeader());

    layout.title.set('Users');
    expect(layout.title()).toBe('Users');

    scope.destroy();

    expect(layout.title()).toBe('');
  });
});

/** 与真实页面同一写法设置标题；页面自己的标题从 h2 起。 */
@Component({
  selector: 'app-test-users-page',
  template: '<h2>Content</h2>',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class UsersPage {
  constructor() {
    const layout = inject(LayoutService);
    //#if (IncludeLocalization)
    const title = translateSignal('page.users');
    effect(() => layout.title.set(title()));
    //#else
    layout.title.set('Users');
    //#endif
  }
}

@Component({
  selector: 'app-test-roles-page',
  template: '<h2>Content</h2>',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class RolesPage {
  constructor() {
    inject(LayoutService).title.set('Roles');
  }
}

@Component({
  selector: 'app-test-untitled-page',
  template: '<h2>Content</h2>',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class UntitledPage {}

/**
 * 两种已登录布局的 h1 都由页头按 `LayoutService.title` 输出（视觉隐藏），未设标题不输出；
 * 抽屉与对话框的名称仍取自各自的标题，不受它影响。
 */
describe('Layout page heading', () => {
  async function setUp(): Promise<RouterTestingHarness> {
    // 与标题无关、又各自依赖会话与后端的部件不渲染
    TestBed.overrideComponent(DefaultLayout, {
      remove: { imports: [DefaultSidebar] },
      add: { schemas: [NO_ERRORS_SCHEMA] },
    });
    // prettier-ignore
    TestBed.overrideComponent(DefaultHeader, {
      remove: {
        imports: [
          UserMenu,
          ThemeModeToggle,
          //#if (IncludeNotifications)
          Notifications,
          //#endif
          //#if (IncludeLocalization)
          LanguageSwitcher,
          //#endif
        ],
      },
      add: { schemas: [NO_ERRORS_SCHEMA] },
    });
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        provideRouter([
          {
            path: 'platform',
            component: DefaultLayout,
            children: [
              { path: 'users', component: UsersPage },
              { path: 'roles', component: RolesPage },
            ],
          },
          {
            path: 'workspace',
            component: WorkspaceLayout,
            children: [
              { path: 'users', component: UsersPage },
              { path: 'untitled', component: UntitledPage },
            ],
          },
        ]),
        // 工作空间顶栏菜单按权限裁剪，这里全部放行
        {
          provide: AuthorizationService,
          useValue: { hasAny: () => true, canAccessPlatform: () => true },
        },
        //#if (Impersonation)
        {
          provide: ImpersonationService,
          useValue: {
            status: () => ({ isImpersonating: false }),
            notifyAfterRenderIfJustExited: () => undefined,
          },
        },
        //#endif
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en', 'zh-CN']),
        //#endif
      ],
    });
    //#if (IncludeLocalization)
    const transloco = TestBed.inject(TranslocoService);
    transloco.setTranslation({ page: { users: 'Users' } }, 'en');
    transloco.setTranslation({ page: { users: '用户管理' } }, 'zh-CN');
    //#endif
    return RouterTestingHarness.create();
  }

  async function visit(harness: RouterTestingHarness, url: string): Promise<void> {
    await harness.navigateByUrl(url);
    await harness.fixture.whenStable();
  }

  /** 按文档顺序列出全部标题：缺 h1、多出 h1 与跳级都会体现在这里。 */
  function headings(harness: RouterTestingHarness): string[] {
    const host = harness.fixture.nativeElement as HTMLElement;
    return [...host.querySelectorAll('h1, h2, h3, h4, h5, h6')].map(
      (heading) => `${heading.tagName.toLowerCase()} ${heading.textContent?.trim()}`,
    );
  }

  it.each(['/platform/users', '/workspace/users'])(
    'outputs the page title as the only h1, visually hidden, at %s',
    async (url) => {
      const harness = await setUp();
      await visit(harness, url);

      expect(headings(harness)).toEqual(['h1 Users', 'h2 Content']);
      const h1 = (harness.fixture.nativeElement as HTMLElement).querySelector('h1')!;
      expect(h1.classList).toContain('sr-only');
      expect(h1.hasAttribute('aria-hidden')).toBe(false);
    },
  );

  it('follows route changes and outputs no h1 for a page without a title', async () => {
    const harness = await setUp();
    await visit(harness, '/platform/users');

    await visit(harness, '/platform/roles');
    expect(headings(harness)).toEqual(['h1 Roles', 'h2 Content']);

    // 换布局时页头随旧布局销毁并清空标题，上一页的标题不会带过来
    await visit(harness, '/workspace/untitled');
    expect(headings(harness)).toEqual(['h2 Content']);
  });
  //#if (IncludeLocalization)

  it('follows the active language', async () => {
    const harness = await setUp();
    await visit(harness, '/workspace/users');

    TestBed.inject(TranslocoService).setActiveLang('zh-CN');
    await harness.fixture.whenStable();

    expect(headings(harness)).toEqual(['h1 用户管理', 'h2 Content']);
  });
  //#endif

  it('leaves sheet and dialog names to their own titles', async () => {
    const harness = await setUp();
    await visit(harness, '/workspace/users');
    const host = harness.fixture.nativeElement as HTMLElement;
    const body = host.ownerDocument.body;
    // 浮层挂在 document 上；名称取 aria-labelledby 指向的元素
    const nameOf = (overlay: Element) =>
      body.ownerDocument.getElementById(overlay.getAttribute('aria-labelledby')!)?.textContent;

    // 窄屏导航抽屉
    host.querySelector<HTMLButtonElement>('[data-testid="workspace-nav-trigger"]')!.click();
    await vi.waitFor(() => expect(body.querySelector('[role="dialog"]')).not.toBeNull());
    const sheet = body.querySelector('[role="dialog"]')!;
    expect(nameOf(sheet)?.trim()).toBe(sheet.querySelector('h2')!.textContent!.trim());

    void TestBed.inject(ConfirmService).open({ header: 'Delete user', message: 'Sure?' });
    await vi.waitFor(() => expect(body.querySelector('[role="alertdialog"]')).not.toBeNull());
    expect(nameOf(body.querySelector('[role="alertdialog"]')!)?.trim()).toBe('Delete user');

    expect(headings(harness)[0]).toBe('h1 Users');
    body.querySelectorAll('.cdk-overlay-container').forEach((container) => container.remove());
  });
});
