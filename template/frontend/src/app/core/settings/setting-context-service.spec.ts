import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { SettingContextService } from './setting-context-service';
import { SettingOutputDto } from './setting.dto';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../i18n/transloco.testing';
import { LanguageService } from '../services/language-service';
//#endif

function setting(overrides: Partial<SettingOutputDto> = {}): SettingOutputDto {
  return {
    name: 'Display.TimeZone',
    displayName: '时区',
    group: 'Display',
    groupDisplayName: '显示',
    userValue: null,
    tenantValue: null,
    defaultValue: 'Asia/Shanghai',
    allowsTenantScope: true,
    allowsUserScope: true,
    allowsHostScope: false,
    minimum: null,
    maximum: null,
    canManage: false,
    ...overrides,
  };
}

describe('SettingContextService', () => {
  let service: SettingContextService;
  let http: HttpTestingController;

  beforeEach(() => {
    //#if (IncludeLocalization)
    // 显式定住本设备语言：不定的话初始活动语言会跟随**运行测试那台机器**的系统语言，
    // 断言 locale 初值的用例就成了环境依赖。
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    //#endif
    TestBed.configureTestingModule({
      //#if (IncludeLocalization)
      providers: [
        SettingContextService,
        provideHttpClient(),
        provideHttpClientTesting(),
        // 日期书写用的 locale 取自活动语言，本服务因此依赖 LanguageService。
        ...provideTranslocoTesting(['en', 'zh-CN']),
      ],
      //#else
      providers: [SettingContextService, provideHttpClient(), provideHttpClientTesting()],
      //#endif
    });
    service = TestBed.inject(SettingContextService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    //#if (IncludeLocalization)
    localStorage.removeItem(LanguageService.STORAGE_KEY);
    //#endif
  });

  it('resolves values by user → tenant → default', async () => {
    const load = service.load();
    http
      .expectOne('/api/v1/settings')
      .flush([setting({ userValue: 'Asia/Tokyo', tenantValue: 'UTC' })]);
    await load;

    expect(service.timeZone()).toBe('Asia/Tokyo');
  });

  it('falls back to the tenant value, then the code default', async () => {
    const first = service.load();
    http.expectOne('/api/v1/settings').flush([setting({ tenantValue: 'UTC' })]);
    await first;
    expect(service.timeZone()).toBe('UTC');

    const second = service.load();
    http.expectOne('/api/v1/settings').flush([setting()]);
    await second;
    expect(service.timeZone()).toBe('Asia/Shanghai');
  });

  // 失败必须向上传播、且保留上一份有效快照：在这里吞掉并清空，会让「保存成功后
  // 刷新失败」表现成页面变空、时区退回浏览器，看起来像是保存本身出了问题。
  it('keeps the last good snapshot when a reload fails', async () => {
    const first = service.load();
    http.expectOne('/api/v1/settings').flush([setting({ userValue: 'Asia/Tokyo' })]);
    await first;

    const failing = service.load();
    http.expectOne('/api/v1/settings').flush('boom', { status: 500, statusText: 'Server Error' });

    await expect(failing).rejects.toThrow();
    expect(service.timeZone()).toBe('Asia/Tokyo');
  });

  //#if (IncludeLocalization)
  /** 日期书写用的 locale 取自活动语言而非语言设置：从快照推导会让文案与日期分叉。 */
  it('derives the date locale from the active language, not the settings snapshot', async () => {
    const language = TestBed.inject(LanguageService);
    expect(service.displayLocale()).toBe('en');

    await language.applyAccountLang('zh-CN');
    expect(service.displayLocale()).toBe('zh-CN');

    // 快照里那一项仍是旧值也不影响：把设置应用到活动语言由会话上下文负责。
    const load = service.load();
    http
      .expectOne('/api/v1/settings')
      .flush([setting({ name: 'Display.Language', userValue: 'en', defaultValue: 'en' })]);
    await load;

    expect(service.displayLocale()).toBe('zh-CN');
  });

  //#endif
  // 晚到的旧响应不能覆盖较新的快照：设置页保存与页头切换语言可能各发一次读取
  it('keeps the newer snapshot when an earlier load answers late', async () => {
    const earlier = service.load();
    const later = service.load();
    const [first, second] = http.match('/api/v1/settings');

    second.flush([setting({ userValue: 'Asia/Tokyo' })]);
    await later;
    first.flush([setting({ userValue: 'Europe/Paris' })]);
    await earlier;

    expect(service.timeZone()).toBe('Asia/Tokyo');
  });

  // 主体离开前发出的读取晚到：不能把上一个人的快照写回来
  it('ignores a load that answers after sign-out', async () => {
    const load = service.load();
    service.clear();
    http.expectOne('/api/v1/settings').flush([setting({ userValue: 'Asia/Tokyo' })]);
    await load;

    expect(service.timeZone()).toBeUndefined();
  });

  it('clears the snapshot on sign-out', async () => {
    const load = service.load();
    http.expectOne('/api/v1/settings').flush([setting({ userValue: 'Asia/Tokyo' })]);
    await load;

    service.clear();

    expect(service.timeZone()).toBeUndefined();
  });
});
