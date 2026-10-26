import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { of, Subject, throwError } from 'rxjs';

import { LanguageSwitcher } from './language-switcher';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../i18n/transloco.testing';
//#endif
import { AuthService } from '../../services/auth-service';
import { LanguageService } from '../../services/language-service';
import { SessionContextService } from '../../services/session-context-service';
import { SettingContextService } from '../../settings/setting-context-service';
import { SettingService } from '../../settings/setting-service';

import type { MockedObject } from 'vitest';

/** 语言选择的归属由切换器决定：已登录的选择写回账户设置，未登录的才落本地存储。 */
describe('LanguageSwitcher', () => {
  let component: LanguageSwitcher;
  let authService: Pick<MockedObject<AuthService>, 'isAuthenticated'>;
  let settingService: Pick<MockedObject<SettingService>, 'setForCurrentUser'>;
  let sessionContext: Pick<MockedObject<SessionContextService>, 'refreshSettings'>;

  function setUp(authenticated: boolean): void {
    authService = {
      isAuthenticated: vi.fn().mockName('AuthService.isAuthenticated'),
    };
    authService.isAuthenticated.mockReturnValue(authenticated);
    settingService = {
      setForCurrentUser: vi.fn().mockName('SettingService.setForCurrentUser'),
    };
    settingService.setForCurrentUser.mockReturnValue(of(undefined));
    sessionContext = {
      refreshSettings: vi.fn().mockName('SessionContextService.refreshSettings'),
    };
    sessionContext.refreshSettings.mockResolvedValue([]);

    TestBed.configureTestingModule({
      imports: [LanguageSwitcher],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // 真实 AuthService 会拉起认证栈；这里只关心「已登录还是没登录」。
        { provide: AuthService, useValue: authService },
        { provide: SettingService, useValue: settingService },
        { provide: SessionContextService, useValue: sessionContext },
        ...provideTranslocoTesting(['en', 'zh-CN']),
      ],
    });
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    component = TestBed.createComponent(LanguageSwitcher).componentInstance;
  }

  afterEach(() => localStorage.removeItem(LanguageService.STORAGE_KEY));

  it('writes an authenticated choice to the account without touching the device preference', async () => {
    setUp(true);

    component.select('zh-CN');
    await vi.waitFor(() => expect(TestBed.inject(LanguageService).activeLang()).toBe('zh-CN'));

    expect(settingService.setForCurrentUser).toHaveBeenCalledWith({
      name: 'Display.Language',
      value: 'zh-CN',
    });
    expect(TestBed.inject(LanguageService).activeLang()).toBe('zh-CN');
    // 落盘就分不清「这台机器的偏好」和「上一个登录者的偏好」了。
    expect(localStorage.getItem(LanguageService.STORAGE_KEY)).toBe('en');
  });

  /** 切语言后文案与日期同时换：日期写法取自活动语言（见 SettingContextService.displayLocale）。 */
  it('switches text and dates together once the account write succeeds', async () => {
    setUp(true);
    const settingContext = TestBed.inject(SettingContextService);
    expect(settingContext.displayLocale()).toBe('en');

    component.select('zh-CN');
    await vi.waitFor(() => expect(TestBed.inject(LanguageService).activeLang()).toBe('zh-CN'));

    expect(settingContext.displayLocale()).toBe('zh-CN');
  });

  /** 先写回、成功后再切换：先切换会让设置页的重取早于写入完成，偏好页显示旧值。 */
  it('does not switch before the account write completes', () => {
    setUp(true);
    const pending = new Subject<void>();
    settingService.setForCurrentUser.mockReturnValue(pending);

    component.select('zh-CN');

    expect(TestBed.inject(LanguageService).activeLang()).toBe('en');
  });

  it('keeps the current language and says so when the account write fails', () => {
    setUp(true);
    settingService.setForCurrentUser.mockReturnValue(throwError(() => new Error('network down')));
    const error = vi.spyOn(toast, 'error').mockImplementation(() => '');

    component.select('zh-CN');

    // 不切换，界面与账户偏好就不会分叉；但必须说出来，否则用户以为存好了。
    expect(TestBed.inject(LanguageService).activeLang()).toBe('en');
    expect(error).toHaveBeenCalled();
  });

  // 写入成功后刷新设置快照：偏好页与按快照解析的消费端要看到新值
  it('refreshes the settings snapshot after the account write succeeds', async () => {
    setUp(true);

    component.select('zh-CN');

    await vi.waitFor(() => expect(sessionContext.refreshSettings).toHaveBeenCalledOnce());
  });

  it('keeps the new language when refreshing the snapshot fails', async () => {
    setUp(true);
    sessionContext.refreshSettings.mockRejectedValue(new Error('network down'));
    const error = vi.spyOn(toast, 'error').mockImplementation(() => '');

    component.select('zh-CN');

    await vi.waitFor(() => expect(sessionContext.refreshSettings).toHaveBeenCalledOnce());
    await vi.waitFor(() => expect(TestBed.inject(LanguageService).activeLang()).toBe('zh-CN'));
    expect(error).not.toHaveBeenCalled();
  });

  it('does not refresh the snapshot when the account write fails', () => {
    setUp(true);
    settingService.setForCurrentUser.mockReturnValue(throwError(() => new Error('network down')));
    vi.spyOn(toast, 'error').mockImplementation(() => '');

    component.select('zh-CN');

    expect(sessionContext.refreshSettings).not.toHaveBeenCalled();
  });

  it('records an anonymous choice as the device preference and writes no setting', async () => {
    setUp(false);

    component.select('zh-CN');

    // 访客没有账户可写；不落盘的话这次选择一刷新就没了。
    expect(localStorage.getItem(LanguageService.STORAGE_KEY)).toBe('zh-CN');
    await vi.waitFor(() => expect(TestBed.inject(LanguageService).activeLang()).toBe('zh-CN'));
    expect(settingService.setForCurrentUser).not.toHaveBeenCalled();
  });

  // 只支持一种语言时没有可切换的：整个切换器隐藏，不在操作区留空位
  it('hides itself when only one language is supported', () => {
    TestBed.configureTestingModule({
      imports: [LanguageSwitcher],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { isAuthenticated: () => false } },
        ...provideTranslocoTesting(['en']),
      ],
    });
    const languages = TestBed.inject(LanguageService);
    Object.defineProperty(languages, 'options', { value: [languages.options[0]] });

    const fixture = TestBed.createComponent(LanguageSwitcher);
    fixture.detectChanges();

    // 断言实际样式而非类名：两个设 display 的类同时存在时，类名在、按钮却照样显示
    const host = fixture.nativeElement as HTMLElement;
    expect(getComputedStyle(host).display).toBe('none');
    expect(host.getBoundingClientRect().width).toBe(0);
  });

  it('shows itself when more than one language is supported', () => {
    setUp(false);
    const fixture = TestBed.createComponent(LanguageSwitcher);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(getComputedStyle(host).display).toBe('inline-flex');
    expect(host.getBoundingClientRect().width).toBeGreaterThan(0);
  });
});
