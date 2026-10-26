import { BreakpointObserver } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { of, Subject, throwError } from 'rxjs';

import { ThemeModeToggle } from './theme-mode-toggle';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../i18n/transloco.testing';
//#endif
import { AuthService } from '../../services/auth-service';
import { SessionContextService } from '../../services/session-context-service';
import { ThemeService } from '../../services/theme-service';
import { SettingService } from '../../settings/setting-service';

import type { MockedObject } from 'vitest';

/** 外观的归属由切换器决定：已登录的选择写回账户设置，未登录的只记在本设备。 */
describe('ThemeModeToggle', () => {
  let component: ThemeModeToggle;
  let theme: ThemeService;
  let settingService: Pick<MockedObject<SettingService>, 'setForCurrentUser'>;
  let sessionContext: Pick<MockedObject<SessionContextService>, 'refreshSettings'>;

  function setUp(authenticated: boolean): void {
    settingService = {
      setForCurrentUser: vi.fn().mockName('SettingService.setForCurrentUser'),
    };
    settingService.setForCurrentUser.mockReturnValue(of(undefined));
    sessionContext = {
      refreshSettings: vi.fn().mockName('SessionContextService.refreshSettings'),
    };
    sessionContext.refreshSettings.mockResolvedValue([]);

    TestBed.configureTestingModule({
      imports: [ThemeModeToggle],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // 真实 AuthService 会拉起认证栈；这里只关心「已登录还是没登录」
        { provide: AuthService, useValue: { isAuthenticated: () => authenticated } },
        { provide: SettingService, useValue: settingService },
        { provide: SessionContextService, useValue: sessionContext },
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
    component = TestBed.createComponent(ThemeModeToggle).componentInstance;
  }

  afterEach(() => {
    localStorage.removeItem(ThemeService.STORAGE_KEY);
    document.documentElement.classList.remove('dark');
  });

  it('writes an authenticated choice to the account, then switches', () => {
    setUp(true);

    component.toggle();

    // 默认跟随系统，下一档是暗色
    expect(settingService.setForCurrentUser).toHaveBeenCalledWith({
      name: 'Display.Theme',
      value: 'dark',
    });
    expect(theme.mode()).toBe('dark');
    expect(sessionContext.refreshSettings).toHaveBeenCalledOnce();
  });

  /** 先写回、成功后再切换：先切换会让偏好页的重取早于写入完成，显示旧值。 */
  it('does not switch before the account write completes', () => {
    setUp(true);
    settingService.setForCurrentUser.mockReturnValue(new Subject<void>());

    component.toggle();

    expect(theme.mode()).toBe('system');
  });

  it('keeps the current appearance and says so when the account write fails', () => {
    setUp(true);
    settingService.setForCurrentUser.mockReturnValue(throwError(() => new Error('network down')));
    const error = vi.spyOn(toast, 'error').mockImplementation(() => '');

    component.toggle();

    expect(theme.mode()).toBe('system');
    expect(error).toHaveBeenCalled();
    expect(sessionContext.refreshSettings).not.toHaveBeenCalled();
  });

  it('records an anonymous choice on this device and writes no setting', () => {
    setUp(false);

    component.toggle();

    expect(theme.mode()).toBe('dark');
    expect(settingService.setForCurrentUser).not.toHaveBeenCalled();
  });
});
