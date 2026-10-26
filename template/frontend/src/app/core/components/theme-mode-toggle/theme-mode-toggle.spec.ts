import { BreakpointObserver } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { of } from 'rxjs';

import { ThemeModeToggle } from './theme-mode-toggle';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../i18n/transloco.testing';
//#endif
import { AuthService } from '../../services/auth-service';
import { ThemeService } from '../../services/theme-service';

const URL = '/api/v1/auth/me/preferences';
const NONE = { language: null, timeZone: null, theme: null };

/** 外观的归属由切换器决定：已登录的选择写进账户偏好，未登录的只记在本设备。 */
describe('ThemeModeToggle', () => {
  let component: ThemeModeToggle;
  let theme: ThemeService;
  let http: HttpTestingController;

  function setUp(authenticated: boolean): void {
    TestBed.configureTestingModule({
      imports: [ThemeModeToggle],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // 真实 AuthService 会拉起认证栈；这里只关心「已登录还是没登录」
        { provide: AuthService, useValue: { isAuthenticated: () => authenticated } },
        // 系统偏好恒为亮色、不减少动效：用例与运行测试那台机器的设置无关
        {
          provide: BreakpointObserver,
          useValue: { observe: () => of({ matches: false, breakpoints: {} }) },
        },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
    // 不播放过渡，直接执行回调
    vi.spyOn(document, 'startViewTransition').mockImplementation((update) => {
      void (update as () => void)();
      return {} as ViewTransition;
    });
    theme = TestBed.inject(ThemeService);
    http = TestBed.inject(HttpTestingController);
    component = TestBed.createComponent(ThemeModeToggle).componentInstance;
  }

  /** 写入在偏好队列的微任务里发出，同步 expectOne 会扑空。 */
  const settle = () => new Promise((resolve) => setTimeout(resolve));

  afterEach(() => {
    http.verify();
    localStorage.removeItem(ThemeService.STORAGE_KEY);
    document.documentElement.classList.remove('dark');
  });

  // 先写入、按响应切换：先切的话保存失败时界面与账户就分叉了
  it('writes an authenticated choice to the account, then switches', async () => {
    setUp(true);

    component.toggle();
    await settle();

    // 默认跟随系统，下一档是暗色
    const request = http.expectOne(URL);
    expect(request.request.body).toEqual({ theme: 'dark' });
    expect(theme.mode()).toBe('system');

    request.flush({
      preferences: { ...NONE, theme: 'dark' },
      preferenceOverrides: { ...NONE, theme: 'dark' },
    });
    await vi.waitFor(() => expect(theme.mode()).toBe('dark'));
  });

  it('keeps the current appearance and says so when the account rejects it', async () => {
    setUp(true);
    const error = vi.spyOn(toast, 'error').mockImplementation(() => '');

    component.toggle();
    await settle();
    http
      .expectOne(URL)
      .flush({ code: 'Setting:ValueNotAllowed' }, { status: 400, statusText: 'Bad Request' });

    await vi.waitFor(() => expect(error).toHaveBeenCalled());
    expect(theme.mode()).toBe('system');
  });

  // 资源服务转交失败（依赖故障）：可能已经保存，提示未能确认，不说成未保存，也不改界面
  it('reports an unconfirmed save when the account service cannot be reached', async () => {
    setUp(true);
    const warning = vi.spyOn(toast, 'warning').mockImplementation(() => '');

    component.toggle();
    await settle();
    http.expectOne(URL).flush(null, { status: 503, statusText: 'Service Unavailable' });

    await vi.waitFor(() => expect(warning).toHaveBeenCalled());
    expect(theme.mode()).toBe('system');
  });

  it('records an anonymous choice on this device and writes no setting', () => {
    setUp(false);

    component.toggle();

    expect(theme.mode()).toBe('dark');
    http.expectNone(URL);
  });
});
