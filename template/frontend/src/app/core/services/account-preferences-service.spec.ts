import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AccountPreferencesService } from './account-preferences-service';
//#if (IncludeLocalization)
import { LanguageService } from './language-service';
//#endif
import { ThemeService } from './theme-service';
import {
  AccountPreferencesDto,
  AccountPreferencesOutputDto,
} from '../../shared/dtos/account-preferences.dto';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../i18n/transloco.testing';
//#endif

const URL = '/api/v1/auth/me/preferences';
const NONE: AccountPreferencesDto = { language: null, timeZone: null, theme: null };

function knownOf(
  preferences: Partial<AccountPreferencesDto>,
  overrides: Partial<AccountPreferencesDto> = preferences,
): AccountPreferencesOutputDto {
  return {
    preferences: { ...NONE, ...preferences },
    preferenceOverrides: { ...NONE, ...overrides },
  };
}

const UNAVAILABLE: AccountPreferencesOutputDto = { preferences: null, preferenceOverrides: null };

/** 账户偏好状态：读取的新旧判断、新主体与同主体的不可用处理、写入队列与结果分类。 */
describe('AccountPreferencesService', () => {
  let service: AccountPreferencesService;
  let http: HttpTestingController;

  beforeEach(() => {
    //#if (IncludeLocalization)
    // 显式定住本设备语言，否则初始语言跟随运行测试那台机器的系统语言。
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    //#endif
    TestBed.configureTestingModule({
      //#if (IncludeLocalization)
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        ...provideTranslocoTesting(['en', 'zh-CN']),
      ],
      //#else
      providers: [provideHttpClient(), provideHttpClientTesting()],
      //#endif
    });
    // 本人写入的外观带过渡切换：替身直接执行回调，不真的播放过渡
    vi.spyOn(document, 'startViewTransition').mockImplementation((update) => {
      void (update as () => void)();
      return {} as ViewTransition;
    });
    service = TestBed.inject(AccountPreferencesService);
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

  const read = (value: AccountPreferencesOutputDto, kind: 'establish' | 'refresh' = 'establish') =>
    service.applyRead(service.beginRead(), value, kind);

  /** 让队列里的下一项发出：写入在微任务里排队，同步 expectOne 会扑空。 */
  const settle = () => new Promise((resolve) => setTimeout(resolve));

  describe('reading', () => {
    it('applies the time zone, appearance and language it reads', async () => {
      //#if (IncludeLocalization)
      await read(knownOf({ language: 'zh-CN', timeZone: 'Asia/Tokyo', theme: 'dark' }));

      expect(TestBed.inject(LanguageService).activeLang()).toBe('zh-CN');
      //#else
      await read(knownOf({ timeZone: 'Asia/Tokyo', theme: 'dark' }));
      //#endif
      expect(service.timeZone()).toBe('Asia/Tokyo');
      expect(TestBed.inject(ThemeService).mode()).toBe('dark');
      expect(service.known()).toBe(true);
    });

    // 字段为 null 是"已知未设置"：时区按浏览器，外观跟随系统
    it('treats unset fields as following the device', async () => {
      TestBed.inject(ThemeService).setMode('dark');

      await read(knownOf({}));

      expect(service.known()).toBe(true);
      expect(service.timeZone()).toBeUndefined();
      expect(TestBed.inject(ThemeService).mode()).toBe('system');
    });

    // 新主体读不到偏好：清掉前一人的，退回设备
    it('drops the previous preferences when a new subject cannot read them', async () => {
      //#if (IncludeLocalization)
      await read(knownOf({ language: 'zh-CN', timeZone: 'Asia/Tokyo' }));
      //#else
      await read(knownOf({ timeZone: 'Asia/Tokyo' }));
      //#endif

      await read(UNAVAILABLE, 'establish');

      expect(service.known()).toBe(false);
      expect(service.timeZone()).toBeUndefined();
      //#if (IncludeLocalization)
      expect(TestBed.inject(LanguageService).activeLang()).toBe('en');
      //#endif
    });

    // 同一主体后台读不到：保留已应用的值，不显示成"未设置"
    it('keeps the applied preferences when a background re-read cannot load them', async () => {
      await read(knownOf({ timeZone: 'Asia/Tokyo' }));

      await read(UNAVAILABLE, 'refresh');

      expect(service.known()).toBe(true);
      expect(service.timeZone()).toBe('Asia/Tokyo');
    });

    // 后端忽略 null：未设置的字段不出现，缺省与 null 同义
    it('treats omitted fields inside a known read as unset', async () => {
      await service.applyRead(
        service.beginRead(),
        { preferences: { timeZone: 'Asia/Tokyo', theme: 'dark' }, preferenceOverrides: {} },
        'establish',
      );

      expect(service.known()).toBe(true);
      expect(service.timeZone()).toBe('Asia/Tokyo');
      expect(service.overrides()).toEqual({ language: null, timeZone: null, theme: null });
    });

    // 其他返回当前用户的接口不带偏好字段：缺失不能当作读到了"未设置"
    it('treats missing fields as unavailable rather than unset', async () => {
      await read(knownOf({ timeZone: 'Asia/Tokyo' }));

      await service.applyRead(service.beginRead(), {}, 'refresh');

      expect(service.timeZone()).toBe('Asia/Tokyo');
    });

    // 读取发出后本人写入成功：迟到的旧 /me 不能盖掉写入结果
    it('ignores a read that answers after a write succeeded', async () => {
      await read(knownOf({ timeZone: 'UTC' }));
      const ticket = service.beginRead();

      const write = service.write('timeZone', 'Asia/Tokyo');
      await settle();
      http.expectOne(URL).flush(knownOf({ timeZone: 'Asia/Tokyo' }));
      await write;
      await service.applyRead(ticket, knownOf({ timeZone: 'UTC' }), 'refresh');

      expect(service.timeZone()).toBe('Asia/Tokyo');
    });

    // 读取发出后主体离开：上一个人的偏好不能被写回来
    it('ignores a read that answers after the subject left', async () => {
      const ticket = service.beginRead();
      await service.clear();

      await service.applyRead(ticket, knownOf({ timeZone: 'Asia/Tokyo' }), 'establish');

      expect(service.known()).toBe(false);
    });
  });

  describe('writing', () => {
    it('sends one field as a merge patch and applies the full response', async () => {
      await read(knownOf({ timeZone: 'Asia/Tokyo' }, { timeZone: 'Asia/Tokyo' }));

      const write = service.write('timeZone', null);
      await settle();
      const request = http.expectOne(URL);
      expect(request.request.method).toBe('PATCH');
      expect(request.request.body).toEqual({ timeZone: null });
      expect(request.request.headers.get('Content-Type')).toBe('application/merge-patch+json');
      // 清除个人覆盖：生效值回落到组织默认，只有服务端知道是哪个
      request.flush(knownOf({ timeZone: 'Europe/London' }, { timeZone: null }));

      await expect(write).resolves.toEqual({ status: 'saved' });
      expect(service.timeZone()).toBe('Europe/London');
      expect(service.overrides()?.timeZone).toBeNull();
    });

    // 面板与页头各自发起的写入：响应乱序时，先完成的旧完整表示会盖掉后完成的新值，所以串行
    it('sends the next write only after the previous one has been applied', async () => {
      const first = service.write('theme', 'dark');
      const second = service.write('timeZone', 'Asia/Tokyo');
      await settle();

      const requests = http.match(URL);
      expect(requests).toHaveLength(1);
      requests[0].flush(knownOf({ theme: 'dark' }));
      await first;
      await settle();

      http.expectOne(URL).flush(knownOf({ theme: 'dark', timeZone: 'Asia/Tokyo' }));
      await second;

      expect(TestBed.inject(ThemeService).mode()).toBe('dark');
      expect(service.timeZone()).toBe('Asia/Tokyo');
    });

    it('keeps the queue moving after a rejected write', async () => {
      await read(knownOf({ timeZone: 'UTC' }));
      const first = service.write('timeZone', 'Mars/Base');
      const second = service.write('theme', 'dark');
      await settle();

      http
        .expectOne(URL)
        .flush(
          { code: 'AppSetting:TimeZoneInvalid', detail: 'Invalid time zone' },
          { status: 400, statusText: 'Bad Request' },
        );
      const outcome = await first;
      await settle();
      http.expectOne(URL).flush(knownOf({ timeZone: 'UTC', theme: 'dark' }));

      expect(outcome.status).toBe('rejected');
      await expect(second).resolves.toEqual({ status: 'saved' });
      expect(service.timeZone()).toBe('UTC');
    });

    // 没有可判定的响应：可能已经保存，偏好不变，等下次重查对齐
    for (const status of [0, 502, 503, 504]) {
      it(`reports an unconfirmed save without changing preferences on status ${status}`, async () => {
        await read(knownOf({ timeZone: 'UTC' }));

        const write = service.write('timeZone', 'Asia/Tokyo');
        await settle();
        if (status === 0) {
          http.expectOne(URL).error(new ProgressEvent('error'));
        } else {
          http.expectOne(URL).flush(null, { status, statusText: 'Upstream' });
        }

        expect((await write).status).toBe('unconfirmed');
        expect(service.timeZone()).toBe('UTC');
      });
    }

    // 主体离开：排着队还没发出的写入属于上一个人，不能发给下一个人
    it('discards queued writes when the subject leaves', async () => {
      const first = service.write('theme', 'dark');
      const second = service.write('timeZone', 'Asia/Tokyo');
      await settle();

      void service.clear();
      http.expectOne(URL).flush(knownOf({ theme: 'dark' }));

      await expect(first).resolves.toEqual({ status: 'discarded' });
      await expect(second).resolves.toEqual({ status: 'discarded' });
      await settle();
      http.expectNone(URL);
      expect(service.known()).toBe(false);
    });

    // 失败回来时主体已经离开：不报拒绝或未确认，否则提示会落到下一个人的界面上
    for (const status of [400, 503]) {
      it(`discards a write that fails after the subject left on status ${status}`, async () => {
        const write = service.write('theme', 'dark');
        await settle();

        void service.clear();
        http.expectOne(URL).flush(null, { status, statusText: 'Failed' });

        await expect(write).resolves.toEqual({ status: 'discarded' });
      });
    }
  });
  //#if (IncludeLocalization)

  /** 日期书写用的 locale 取自活动语言而非偏好：偏好未设置时界面按设备语言显示。 */
  it('derives the date locale from the active language', async () => {
    expect(service.displayLocale()).toBe('en');

    await TestBed.inject(LanguageService).applyAccountLang('zh-CN');

    expect(service.displayLocale()).toBe('zh-CN');
  });
  //#endif
});
