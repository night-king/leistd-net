import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';

import { AccountPreferencesService } from './account-preferences-service';
import { AuthService } from './auth-service';
import { AuthorizationService } from './authorization-service';
//#if (IncludeLocalization)
import { LanguageService } from './language-service';
//#endif
import { SessionContextService } from './session-context-service';
import { ThemeService } from './theme-service';
import {
  AccountPreferencesDto,
  AccountPreferencesOutputDto,
} from '../../shared/dtos/account-preferences.dto';
import { UserOutputDto } from '../../shared/dtos/auth.dto';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../i18n/transloco.testing';
//#endif
import { SettingContextService } from '../settings/setting-context-service';
import { SettingOutputDto } from '../settings/setting.dto';

import type { MockedObject } from 'vitest';

/** 设置快照里的一项（账户偏好不在快照里，这里只验设置随主体建立与清空）。 */
function probeSetting(userValue: string | null): SettingOutputDto {
  return {
    name: 'Probe.Value',
    displayName: '探针',
    group: 'Other',
    groupDisplayName: '其他',
    userValue,
    tenantValue: null,
    defaultValue: null,
    allowsTenantScope: true,
    allowsUserScope: true,
    allowsHostScope: false,
    minimum: null,
    maximum: null,
    canManage: false,
  };
}

const NONE: AccountPreferencesDto = { language: null, timeZone: null, theme: null };

/** `/me` 读到的偏好：给出生效值，个人覆盖与之相同。 */
function readOf(preferences: Partial<AccountPreferencesDto>): AccountPreferencesOutputDto {
  const value = { ...NONE, ...preferences };
  return { preferences: value, preferenceOverrides: value };
}

const UNAVAILABLE: AccountPreferencesOutputDto = { preferences: null, preferenceOverrides: null };

/** 会话上下文：登录后由这里载入权限、设置与账户偏好；主体离开时一起清掉。 */
describe('SessionContextService', () => {
  let service: SessionContextService;
  let settingContext: SettingContextService;
  let preferences: AccountPreferencesService;
  let authService: Pick<
    MockedObject<AuthService>,
    | 'clearAuthData'
    | 'isAuthenticated'
    | 'fetchUser'
    | 'setCurrentUser'
    | 'lastReadPreferences'
    | 'revision'
  >;
  let http: HttpTestingController;

  beforeEach(() => {
    //#if (IncludeLocalization)
    // 显式定住本设备语言，否则初始语言跟随运行测试那台机器的系统语言。
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    //#endif
    // 写入序号与真实服务一致：写入或清空当前用户都加一
    let userRevision = 0;
    authService = {
      clearAuthData: vi.fn(() => void userRevision++).mockName('AuthService.clearAuthData'),
      isAuthenticated: vi.fn().mockName('AuthService.isAuthenticated').mockReturnValue(true),
      fetchUser: vi
        .fn()
        .mockName('AuthService.fetchUser')
        .mockReturnValue(of({ username: 'admin' } as UserOutputDto)),
      setCurrentUser: vi.fn(() => void userRevision++).mockName('AuthService.setCurrentUser'),
      // 号在 /me 发起时取：替身在被问到时才取，等同于"刚读完 /me 就建立会话"
      lastReadPreferences: vi
        .fn()
        .mockName('AuthService.lastReadPreferences')
        .mockImplementation(() => ({
          ticket: TestBed.inject(AccountPreferencesService).beginRead(),
          read: readOf({}),
        })),
      get revision() {
        return userRevision;
      },
    };
    TestBed.configureTestingModule({
      //#if (IncludeLocalization)
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // 真实 AuthService 会拉起认证栈（无本地身份的形态下是整个 OIDC 客户端）；
        // 用 spy 替身：既避开那条依赖链，又能断言清理确实把认证数据也带上了。
        { provide: AuthService, useValue: authService },
        ...provideTranslocoTesting(['en', 'zh-CN']),
      ],
      //#else
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // 真实 AuthService 会拉起认证栈（无本地身份的形态下是整个 OIDC 客户端）；
        // 用 spy 替身：既避开那条依赖链，又能断言清理确实把认证数据也带上了。
        { provide: AuthService, useValue: authService },
      ],
      //#endif
    });
    service = TestBed.inject(SessionContextService);
    settingContext = TestBed.inject(SettingContextService);
    preferences = TestBed.inject(AccountPreferencesService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem(ThemeService.STORAGE_KEY);
    document.documentElement.classList.remove('dark');
    //#if (IncludeLocalization)
    localStorage.removeItem(LanguageService.STORAGE_KEY);
    //#endif
  });

  /**
   * 依次完成 establish() 内部的权限与设置请求：两者顺序 await，中间用一次宏任务把链推完，
   * 否则 expectOne 找不到设置请求。
   */
  async function flushEstablish(settings: SettingOutputDto[] | 'fail'): Promise<void> {
    http
      .expectOne('/api/v1/permissions/current')
      .flush({ permissions: [], isSuperAdmin: false, versionToken: 'v1' });
    await new Promise((resolve) => setTimeout(resolve));

    const settingsReq = http.expectOne('/api/v1/settings');
    if (settings === 'fail') {
      settingsReq.flush('boom', { status: 500, statusText: 'Server Error' });
    } else {
      settingsReq.flush(settings);
    }
  }

  /** 模拟 `loadUser()`：此刻取号并留存读到的偏好，之后再建立会话。 */
  function readMe(read: AccountPreferencesOutputDto): void {
    authService.lastReadPreferences.mockReturnValue({ ticket: preferences.beginRead(), read });
  }

  async function establishWith(read: AccountPreferencesOutputDto): Promise<void> {
    readMe(read);
    const establish = service.establish();
    await flushEstablish([]);
    await establish;
  }

  it('loads permissions and settings when a subject is established', async () => {
    const establish = service.establish();
    await flushEstablish([probeSetting('a')]);
    await establish;

    expect(TestBed.inject(AuthorizationService).loaded()).toBe(true);
    expect(settingContext.valueOf('Probe.Value')).toBe('a');
  });

  // 主体已经换人，上一份快照属于上一个用户——加载失败必须清空，不能沿用。
  it('does not carry the previous subject snapshot when the new load fails', async () => {
    const first = service.establish();
    await flushEstablish([probeSetting('a')]);
    await first;

    const second = service.establish();
    await flushEstablish('fail');
    await second;

    expect(settingContext.valueOf('Probe.Value')).toBeUndefined();
  });

  // 偏好取自建立会话前那次 /me：断言界面状态真的换了，而不只是偏好状态里有值
  it('applies the account preferences read with the current user', async () => {
    //#if (IncludeLocalization)
    await establishWith(readOf({ language: 'zh-CN', timeZone: 'Asia/Tokyo', theme: 'dark' }));

    expect(TestBed.inject(LanguageService).activeLang()).toBe('zh-CN');
    //#else
    await establishWith(readOf({ timeZone: 'Asia/Tokyo', theme: 'dark' }));
    //#endif
    expect(TestBed.inject(ThemeService).mode()).toBe('dark');
    expect(preferences.timeZone()).toBe('Asia/Tokyo');
  });

  // 设置读不到不影响偏好：两者来源不同
  it('still applies the preferences when the settings fail to load', async () => {
    readMe(readOf({ timeZone: 'Asia/Tokyo' }));
    const establish = service.establish();
    await flushEstablish('fail');
    await establish;

    expect(preferences.timeZone()).toBe('Asia/Tokyo');
  });

  // 新主体读不到偏好：上一个人的语言与时区不能留下来
  it('drops the previous subject preferences when the new subject cannot read them', async () => {
    //#if (IncludeLocalization)
    await establishWith(readOf({ language: 'zh-CN', timeZone: 'Asia/Tokyo' }));
    //#else
    await establishWith(readOf({ timeZone: 'Asia/Tokyo' }));
    //#endif

    await establishWith(UNAVAILABLE);

    expect(preferences.known()).toBe(false);
    expect(preferences.timeZone()).toBeUndefined();
    //#if (IncludeLocalization)
    expect(TestBed.inject(LanguageService).activeLang()).toBe('en');
    //#endif
  });

  //#if (IncludeLocalization)
  // 共享机器：A 退出后 B 在登录页不能看到 A 的语言
  it('reverts the language to the device preference when the subject leaves', async () => {
    await establishWith(readOf({ language: 'zh-CN' }));

    service.clear();
    await vi.waitFor(() => expect(TestBed.inject(LanguageService).activeLang()).toBe('en'));
  });

  //#endif
  // 外观不含个人信息：主体离开后留着最近一次值，登录页与下次首帧不闪回另一种主题
  it('keeps the last appearance when the subject leaves', async () => {
    await establishWith(readOf({ theme: 'dark' }));

    service.clear();
    await new Promise((resolve) => setTimeout(resolve));

    expect(TestBed.inject(ThemeService).mode()).toBe('dark');
  });

  it('clears permissions, settings and preferences when the subject leaves', async () => {
    readMe(readOf({ timeZone: 'Asia/Tokyo' }));
    const establish = service.establish();
    await flushEstablish([probeSetting('a')]);
    await establish;

    service.clear();

    // 四样都要清掉。只断言其中几样的话，从统一清理里删掉其余一步不会失败。
    expect(authService.clearAuthData).toHaveBeenCalled();
    expect(TestBed.inject(AuthorizationService).loaded()).toBe(false);
    expect(settingContext.valueOf('Probe.Value')).toBeUndefined();
    expect(preferences.known()).toBe(false);
  });

  // 登录后的 /me 已读回，权限与设置还在加载时用户在页头改了时区并保存成功：那份旧 /me 不能把写入结果盖回去
  it('does not let the /me read before establishing undo a write that succeeded meanwhile', async () => {
    readMe(readOf({ timeZone: 'UTC' }));
    const establish = service.establish();

    const write = preferences.write('timeZone', 'Asia/Tokyo');
    // 写入经队列在下一拍发出
    await new Promise((resolve) => setTimeout(resolve));
    http.expectOne('/api/v1/auth/me/preferences').flush(readOf({ timeZone: 'Asia/Tokyo' }));
    expect((await write).status).toBe('saved');
    await flushEstablish([]);
    await establish;

    expect(preferences.timeZone()).toBe('Asia/Tokyo');
  });

  // 建立会话的等待期间主体已经离开：迟到的初始化不能把离开者的偏好重新应用上
  it('does not apply the preferences of a subject that left before establishing finished', async () => {
    readMe(readOf({ timeZone: 'Asia/Tokyo' }));
    const establish = service.establish();

    service.clear();
    await flushEstablish([]);
    await establish;

    expect(preferences.known()).toBe(false);
    expect(preferences.timeZone()).toBeUndefined();
  });

  describe('when the tab becomes visible', () => {
    const becomeVisible = () => document.dispatchEvent(new Event('visibilitychange'));
    const userWith = (read: AccountPreferencesOutputDto) =>
      ({ username: 'admin', ...read }) as UserOutputDto;

    // 别处改了偏好、资料或已经退出：页签回到前台只重查 /me；权限与设置刷新页面时更新
    it('re-reads only the current user and preferences', async () => {
      authService.fetchUser.mockReturnValue(of(userWith(readOf({ timeZone: 'Asia/Tokyo' }))));

      becomeVisible();
      await service.revalidate();

      expect(authService.fetchUser).toHaveBeenCalledOnce();
      expect(authService.setCurrentUser).toHaveBeenCalled();
      expect(preferences.timeZone()).toBe('Asia/Tokyo');
      http.expectNone('/api/v1/permissions/current');
      http.expectNone('/api/v1/settings');
    });

    // 会话已失效：这次 /me 不是静默的，401 由 HTTP 拦截器清会话并转去登录
    it('reads the current user non-silently so an expired session leads to sign-in', async () => {
      becomeVisible();
      await service.revalidate();

      expect(authService.fetchUser).toHaveBeenCalledWith(false);
    });

    // 同一主体后台读不到偏好：保留已应用的值，旧值比设备值更接近本人的选择
    it('keeps the applied preferences when the re-read cannot load them', async () => {
      await establishWith(readOf({ timeZone: 'Asia/Tokyo' }));
      authService.fetchUser.mockReturnValue(of(userWith(UNAVAILABLE)));

      becomeVisible();
      await service.revalidate();

      expect(preferences.timeZone()).toBe('Asia/Tokyo');
    });

    it('does nothing without a signed-in subject', () => {
      authService.isAuthenticated.mockReturnValue(false);

      becomeVisible();

      expect(authService.fetchUser).not.toHaveBeenCalled();
    });

    // 页签来回切换：在途时不重复发
    it('does not start a second round while one is in flight', () => {
      authService.fetchUser.mockReturnValue(new Subject<UserOutputDto>());

      becomeVisible();
      becomeVisible();

      expect(authService.fetchUser).toHaveBeenCalledOnce();
    });

    // 后台动作，用户没有发起任何操作：失败不提示，也不动现有的当前用户与偏好
    it('keeps the current state when the re-read fails', async () => {
      await establishWith(readOf({ timeZone: 'Asia/Tokyo' }));
      authService.setCurrentUser.mockClear();
      authService.fetchUser.mockReturnValue(throwError(() => new Error('boom')));

      becomeVisible();
      await service.revalidate();

      expect(authService.setCurrentUser).not.toHaveBeenCalled();
      expect(preferences.timeZone()).toBe('Asia/Tokyo');
    });

    // 重查途中主体离开：晚到的结果属于上一个人，不能把用户与偏好写回去
    it('does not apply results that answer after the subject leaves', async () => {
      const user = new Subject<UserOutputDto>();
      authService.fetchUser.mockReturnValue(user);

      becomeVisible();
      service.clear();
      user.next(userWith(readOf({ timeZone: 'Asia/Tokyo' })));
      user.complete();
      await service.revalidate();
      await new Promise((resolve) => setTimeout(resolve));

      expect(authService.setCurrentUser).not.toHaveBeenCalled();
      expect(preferences.known()).toBe(false);
    });

    // 在途合并只在同一主体内：换人后下一个人的重查另起，上一个人那次迟到结束也不放掉它
    it('starts a fresh round for the next subject while the previous one is still in flight', async () => {
      const previous = new Subject<UserOutputDto>();
      const next = new Subject<UserOutputDto>();
      authService.fetchUser.mockReturnValueOnce(previous).mockReturnValueOnce(next);

      becomeVisible();
      service.clear();
      becomeVisible();

      expect(authService.fetchUser).toHaveBeenCalledTimes(2);

      previous.next(userWith(readOf({ timeZone: 'Asia/Tokyo' })));
      previous.complete();
      await new Promise((resolve) => setTimeout(resolve));
      becomeVisible();

      expect(authService.fetchUser).toHaveBeenCalledTimes(2);
      expect(authService.setCurrentUser).not.toHaveBeenCalled();

      next.next({ username: 'next', ...readOf({ timeZone: 'Europe/Paris' }) } as UserOutputDto);
      next.complete();
      await service.revalidate();

      expect(authService.setCurrentUser).toHaveBeenCalledOnce();
      expect(authService.setCurrentUser).toHaveBeenLastCalledWith(
        expect.objectContaining({ username: 'next' }),
      );
      expect(preferences.timeZone()).toBe('Europe/Paris');
    });

    // 重查途中本人保存了资料：迟到的旧 /me 不能把新资料盖回去
    it('keeps a profile saved while the re-read was in flight', async () => {
      const user = new Subject<UserOutputDto>();
      authService.fetchUser.mockReturnValue(user);

      becomeVisible();
      authService.setCurrentUser({ username: 'saved' } as UserOutputDto);
      user.next({ username: 'before-save' } as UserOutputDto);
      user.complete();
      await service.revalidate();

      expect(authService.setCurrentUser).toHaveBeenCalledOnce();
      expect(authService.setCurrentUser).toHaveBeenLastCalledWith({ username: 'saved' });
    });
  });
});
