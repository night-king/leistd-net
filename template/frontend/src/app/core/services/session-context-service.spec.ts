import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AuthService } from './auth-service';
import { AuthorizationService } from './authorization-service';
//#if (IncludeLocalization)
import { LanguageService } from './language-service';
//#endif
import { SessionContextService } from './session-context-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../i18n/transloco.testing';
//#endif
import { SettingContextService } from '../settings/setting-context-service';
import { SettingOutputDto } from '../settings/setting.dto';

import type { MockedObject } from 'vitest';

function timeZoneSetting(userValue: string | null): SettingOutputDto {
  return {
    name: 'Display.TimeZone',
    displayName: '时区',
    group: 'Display',
    groupDisplayName: '显示',
    userValue,
    tenantValue: null,
    defaultValue: 'Asia/Shanghai',
    allowsTenantScope: true,
    allowsUserScope: true,
    allowsHostScope: false,
    minimum: null,
    maximum: null,
    canManage: false,
  };
}
//#if (IncludeLocalization)

function languageSetting(userValue: string | null): SettingOutputDto {
  return {
    name: 'Display.Language',
    displayName: '界面语言',
    group: 'Display',
    groupDisplayName: '显示',
    userValue,
    tenantValue: null,
    defaultValue: 'en',
    allowsTenantScope: true,
    allowsUserScope: true,
    allowsHostScope: false,
    minimum: null,
    maximum: null,
    canManage: false,
  };
}
//#endif

/** 会话上下文：登录后由这里载入设置（SPA 跳转不重跑初始化器）；主体离开时权限与设置一起清掉。 */
describe('SessionContextService', () => {
  let service: SessionContextService;
  let settingContext: SettingContextService;
  let authService: Pick<MockedObject<AuthService>, 'clearAuthData'>;
  let http: HttpTestingController;

  beforeEach(() => {
    //#if (IncludeLocalization)
    // 显式定住本设备语言，否则初始语言跟随运行测试那台机器的系统语言。
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    //#endif
    authService = {
      clearAuthData: vi.fn().mockName('AuthService.clearAuthData'),
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
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
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

  it('loads permissions and settings when a subject is established', async () => {
    const establish = service.establish();
    await flushEstablish([timeZoneSetting('Asia/Tokyo')]);
    await establish;

    expect(TestBed.inject(AuthorizationService).loaded()).toBe(true);
    expect(settingContext.timeZone()).toBe('Asia/Tokyo');
  });

  // 主体已经换人，上一份快照属于上一个用户——加载失败必须清空，不能沿用。
  it('does not carry the previous subject snapshot when the new load fails', async () => {
    const first = service.establish();
    await flushEstablish([timeZoneSetting('Asia/Tokyo')]);
    await first;
    expect(settingContext.timeZone()).toBe('Asia/Tokyo');

    const second = service.establish();
    await flushEstablish('fail');
    await second;

    expect(settingContext.timeZone()).toBeUndefined();
  });

  //#if (IncludeLocalization)
  // 语言是从设置派生的：只断言快照里有值不够，得断言界面语言真的换了。
  // 否则删掉整个语言应用逻辑，其它用例照样全绿——那正是「设了不生效」的形态。
  it('applies the language from settings', async () => {
    const languageService = TestBed.inject(LanguageService);
    expect(languageService.activeLang()).toBe('en');

    const establish = service.establish();
    await flushEstablish([languageSetting('zh-CN')]);
    await establish;

    expect(languageService.activeLang()).toBe('zh-CN');
  });

  // 设置页保存后走的是 refreshSettings()，它同样要重新应用语言——
  // 只刷新数据不应用，改完语言界面会停在旧语言。
  it('re-applies the language when settings are refreshed', async () => {
    const languageService = TestBed.inject(LanguageService);

    const refresh = service.refreshSettings();
    http.expectOne('/api/v1/settings').flush([languageSetting('zh-CN')]);
    await refresh;

    expect(languageService.activeLang()).toBe('zh-CN');
  });

  // 语言已落在 LanguageService 上，清快照收不回来；同时钉住账户语言不写进设备存储。
  it('reverts the language to the device preference when the subject leaves', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    const languageService = TestBed.inject(LanguageService);

    try {
      const establish = service.establish();
      await flushEstablish([languageSetting('zh-CN')]);
      await establish;
      expect(languageService.activeLang()).toBe('zh-CN');
      expect(localStorage.getItem(LanguageService.STORAGE_KEY)).toBe('en');

      service.clear();

      // 退回设备偏好也是先加载词条再激活，不同步生效
      await vi.waitFor(() => expect(languageService.activeLang()).toBe('en'));
    } finally {
      localStorage.removeItem(LanguageService.STORAGE_KEY);
    }
  });

  // 新主体的设置没加载上来时同理：快照清了，语言也得退回，不能沿用上一个人的。
  it('reverts the language when the new subject settings fail to load', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    const languageService = TestBed.inject(LanguageService);

    try {
      const first = service.establish();
      await flushEstablish([languageSetting('zh-CN')]);
      await first;
      expect(languageService.activeLang()).toBe('zh-CN');

      const second = service.establish();
      await flushEstablish('fail');
      await second;

      expect(languageService.activeLang()).toBe('en');
    } finally {
      localStorage.removeItem(LanguageService.STORAGE_KEY);
    }
  });

  // 请求成功但缺项或值不受支持时也要退回设备偏好，否则沿用上一个主体的语言。
  for (const [label, settings] of [
    ['缺少语言项', [timeZoneSetting(null)]],
    ['语言值本端不认', [languageSetting('ja')]],
  ] as const) {
    it(`reverts the language when the loaded settings carry no usable language (${label})`, async () => {
      localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
      const languageService = TestBed.inject(LanguageService);

      try {
        const first = service.establish();
        await flushEstablish([languageSetting('zh-CN')]);
        await first;
        expect(languageService.activeLang()).toBe('zh-CN');

        const second = service.establish();
        await flushEstablish([...settings]);
        await second;

        expect(languageService.activeLang()).toBe('en');
      } finally {
        localStorage.removeItem(LanguageService.STORAGE_KEY);
      }
    });
  }

  //#endif
  it('clears permissions and settings when the subject leaves', async () => {
    const establish = service.establish();
    await flushEstablish([timeZoneSetting('Asia/Tokyo')]);
    await establish;

    service.clear();

    // 三样都要清掉。只断言权限和设置的话，从统一清理里删掉认证那一步不会失败。
    expect(authService.clearAuthData).toHaveBeenCalled();
    expect(TestBed.inject(AuthorizationService).loaded()).toBe(false);
    expect(settingContext.timeZone()).toBeUndefined();
  });
});
