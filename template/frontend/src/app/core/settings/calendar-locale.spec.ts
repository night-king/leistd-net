import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BrnCalendarI18nService } from '@spartan-ng/brain/calendar';

import { calendarLocale, provideAppCalendarLocale } from './calendar-locale';
import { SettingContextService } from './setting-context-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../i18n/transloco.testing';
//#endif

/** 日历文案跟随界面写法：中文界面不再出现英文星期与翻月读屏名。 */
describe('calendarLocale', () => {
  it('writes weekdays, the month header and navigation labels in the given locale', () => {
    const zh = calendarLocale('zh-CN', '上个月', '下个月');

    expect(zh.formatWeekdayName!(0)).toBe('周日');
    expect(zh.formatWeekdayName!(1)).toBe('周一');
    expect(zh.formatHeader!(9, 2026)).toBe('2026年10月');
    expect(zh.labelPrevious!()).toBe('上个月');
    expect(zh.labelNext!()).toBe('下个月');
    expect(zh.months!()).toHaveLength(12);
  });

  it('starts the week on the locale convention, Monday for Chinese', () => {
    // 日历组件用 0 = 周日、1 = 周一
    expect(calendarLocale('zh-CN', '', '').firstDayOfWeek!()).toBe(1);
    expect([0, 1]).toContain(calendarLocale('en-US', '', '').firstDayOfWeek!());
  });

  it('keeps working for an unknown locale', () => {
    const unknown = calendarLocale('not a locale', 'Previous month', 'Next month');

    expect(unknown.formatWeekdayName!(0)).toBeTruthy();
    expect(unknown.formatHeader!(0, 2026)).toContain('2026');
  });

  // use() 内部先读后写配置 signal；接线时没隔离读取，effect 会因自己的写入无限重排，整个应用卡住
  it('applies the config once per locale change instead of re-running on its own write', () => {
    const displayLocale = signal('zh-CN');
    const original = BrnCalendarI18nService.prototype.use;
    // 只放行前几次真实写入：回归成自触发时调用次数断言失败，而不是卡死整个测试浏览器
    const use = vi.spyOn(BrnCalendarI18nService.prototype, 'use').mockImplementation(function (
      this: BrnCalendarI18nService,
      config,
    ) {
      if (use.mock.calls.length <= 5) original.call(this, config);
    });
    onTestFinished(() => use.mockRestore());
    TestBed.configureTestingModule({
      providers: [
        { provide: SettingContextService, useValue: { displayLocale } },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        provideAppCalendarLocale(),
      ],
    });
    const i18n = TestBed.inject(BrnCalendarI18nService);

    TestBed.tick();
    TestBed.tick();
    expect(use).toHaveBeenCalledTimes(1);
    expect(i18n.config().formatWeekdayName(1)).toBe('周一');

    displayLocale.set('en-US');
    TestBed.tick();
    TestBed.tick();
    expect(use).toHaveBeenCalledTimes(2);
    expect(i18n.config().formatWeekdayName(1)).toBe('Mon');
  });
});
