import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif

import { SettingSection } from './setting-section';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { SettingOutputDto } from '../../../../core/settings/setting.dto';
import {
  expectTouchTarget,
  setCoarsePointer,
  tapAt,
} from '../../../../shared/utils/coarse-pointer.testing';
import { SettingsPageState } from '../../settings-page-state';

/**
 * 验取值开放的设置链路（文本框）：刻意用不在 `SETTING_CHOICES` 里的设置名，
 * 免得它日后登记候选项（渲染成下拉）时整批变红。
 */
function row(overrides: Partial<SettingOutputDto> = {}): SettingOutputDto {
  return {
    name: 'Display.FreeTextProbe',
    displayName: '自由文本设置',
    group: 'Other',
    groupDisplayName: '其他',
    userValue: null,
    tenantValue: null,
    defaultValue: 'probe-default',
    allowsTenantScope: true,
    allowsUserScope: true,
    allowsHostScope: false,
    minimum: null,
    maximum: null,
    canManage: true,
    ...overrides,
  };
}

/** 用例的公共提供者。{@link SettingsPageState} 一构造就取一次设置，每个用例开头先应答那次 GET。 */
function pageProviders(): unknown[] {
  return [
    provideHttpClient(),
    provideHttpClientTesting(),
    provideRouter([]),
    SettingsPageState,
    // 页面经会话上下文刷新设置，那条链会碰到 AuthService；真实实现会拉起认证栈
    // （无本地身份的形态下是整个 OIDC 客户端），本组用例只关心页面的行为。
    { provide: AuthService, useValue: { clearAuthData: () => undefined } },
    //#if (IncludeLocalization)
    ...provideTranslocoTesting(['en']),
    //#endif
  ];
}

/** 该设置在当前面板上渲染出来了：按每行常驻的重置按钮判定，设置名在界面上刻意不显示。 */
function hasRow(host: HTMLElement, name: string): boolean {
  return host.querySelector(`[data-testid="setting-reset-${name}"]`) !== null;
}

describe('SettingSection', () => {
  let fixture: ComponentFixture<SettingSection>;
  let http: HttpTestingController;

  const input = () =>
    fixture.nativeElement.querySelector(
      '[data-testid="setting-input-Display.FreeTextProbe"]',
    ) as HTMLInputElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    fixture = TestBed.createComponent(SettingSection);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();

    // 构造时的首次载入
    http.expectOne('/api/v1/settings').flush([row()]);
    await fixture.whenStable();
    fixture.detectChanges();
    vi.useFakeTimers({
      toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
    });
  });

  afterEach(() => {
    try {
      http.verify();
    } finally {
      try {
        fixture.destroy();
      } finally {
        vi.clearAllTimers();
        vi.useRealTimers();
      }
    }
  });

  /** 输入并提交：先 `input` 更新草稿，再 `change` 提交，与真人操作顺序一致。 */
  async function typeAndSave(value: string): Promise<void> {
    const field = input();
    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    field.dispatchEvent(new Event('change'));
    // 写入排进串行队列，请求要等一个微任务才发出——同步 expectOne 会扑空
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** 推进假时钟越过"保存中"的最短可见时长；用独立的 400 毫秒期望，不从生产常量计算。 */
  async function settleSave(): Promise<void> {
    await vi.advanceTimersByTimeAsync(400);
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** 完成写入后的 PUT，并把控制权交回微任务队列，让随后的 GET 真正发出。 */
  async function flushSave(): Promise<void> {
    http.expectOne('/api/v1/settings/current-user').flush(null);
    await fixture.whenStable();
  }

  // 写入期间那一行要有状态，而输入框**不跟着禁用**：提交发生在失焦或回车时，
  // 光标往往还在框里，禁用会当场把焦点弹走、接着输入的字也丢了。
  it('shows a saving state on that row without freezing the text input', async () => {
    await typeAndSave('UTC');

    const host = fixture.nativeElement as HTMLElement;
    expect(input().disabled).toBe(false);
    expect(host.querySelector('hlm-spinner')).toBeTruthy();

    await flushSave();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'UTC' })]);
    await vi.advanceTimersByTimeAsync(399);
    fixture.detectChanges();
    expect(host.querySelector('hlm-spinner')).toBeTruthy();
    await vi.advanceTimersByTimeAsync(1);
    await fixture.whenStable();
    fixture.detectChanges();

    // 写完转圈消失，换成一次绿色对勾（用图标判定：hlm-spinner 自己也带 role=status）
    expect(host.querySelector('hlm-spinner')).toBeNull();
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeTruthy();
    await vi.advanceTimersByTimeAsync(1999);
    fixture.detectChanges();
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeTruthy();
    await vi.advanceTimersByTimeAsync(1);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeNull();
  });

  /**
   * 写入期间本行控件照常可用、焦点留在原处：禁用会把焦点弹到页面上，键盘与读屏用户得从头找回来。
   * 以每行都有的重置按钮断言。
   */
  it('keeps the row controls enabled and focused while it saves', async () => {
    const resetButton = () =>
      (fixture.nativeElement as HTMLElement).querySelector(
        '[data-testid="setting-reset-Display.FreeTextProbe"]',
      ) as HTMLButtonElement;

    resetButton().focus();
    resetButton().click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('hlm-spinner')).toBeTruthy();
    expect(resetButton().disabled).toBe(false);
    expect(document.activeElement).toBe(resetButton());

    await flushSave();
    http.expectOne('/api/v1/settings').flush([row()]);
    await settleSave();

    expect(document.activeElement).toBe(resetButton());
  });

  // 写入在途时再改一次：两次按序写入，以最后一次为准。前一次刷新的快照不能把控件拨回旧值，
  // 也不能提前报"已保存"——那时最后一次还没写完。
  it('queues a second change on the same row and reports only the latest write', async () => {
    const host = fixture.nativeElement as HTMLElement;
    await typeAndSave('UTC');
    await typeAndSave('Asia/Tokyo');

    const first = http.expectOne('/api/v1/settings/current-user');
    expect(first.request.body).toEqual({ name: 'Display.FreeTextProbe', value: 'UTC' });
    first.flush(null);
    await fixture.whenStable();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'UTC' })]);
    await vi.advanceTimersByTimeAsync(400);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(input().value).toBe('Asia/Tokyo');
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeNull();
    expect(host.querySelector('hlm-spinner')).toBeTruthy();

    const second = http.expectOne('/api/v1/settings/current-user');
    expect(second.request.body).toEqual({ name: 'Display.FreeTextProbe', value: 'Asia/Tokyo' });
    second.flush(null);
    await fixture.whenStable();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'Asia/Tokyo' })]);
    await settleSave();

    expect(input().value).toBe('Asia/Tokyo');
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeTruthy();
  });

  // 前一次写入补足"保存中"的等待期间又提交了一次：等待结束时前一次的结果不能盖住正在保存的这一次
  it('keeps the saving state when an earlier write finishes waiting while a later one is pending', async () => {
    const host = fixture.nativeElement as HTMLElement;
    await typeAndSave('UTC');
    await flushSave();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'UTC' })]);
    await vi.advanceTimersByTimeAsync(100);

    await typeAndSave('Asia/Tokyo');
    await vi.advanceTimersByTimeAsync(300);
    fixture.detectChanges();

    expect(host.querySelector('hlm-spinner')).toBeTruthy();
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeNull();
    const second = http.expectOne('/api/v1/settings/current-user');
    expect(second.request.body).toEqual({ name: 'Display.FreeTextProbe', value: 'Asia/Tokyo' });
    second.flush(null);
    await fixture.whenStable();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'Asia/Tokyo' })]);
    await settleSave();
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeTruthy();
  });

  it('does not report an earlier failure while a later write of the row is pending', async () => {
    const host = fixture.nativeElement as HTMLElement;
    await typeAndSave('UTC');
    http
      .expectOne('/api/v1/settings/current-user')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    await vi.advanceTimersByTimeAsync(100);

    await typeAndSave('Asia/Tokyo');
    await vi.advanceTimersByTimeAsync(300);
    fixture.detectChanges();

    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(host.querySelector('hlm-spinner')).toBeTruthy();
    await flushSave();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'Asia/Tokyo' })]);
    await settleSave();
    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(input().value).toBe('Asia/Tokyo');
  });

  // A→B→A：第一笔 A 写完时，草稿里的 A 属于第三笔，不能被当成自己的清掉，否则第二笔的快照把控件拨回 B
  it('keeps the latest value through a write sequence that returns to an earlier value', async () => {
    await typeAndSave('UTC');
    await typeAndSave('Asia/Tokyo');
    await typeAndSave('UTC');

    const writes: string[] = [];
    for (const snapshot of ['UTC', 'Asia/Tokyo']) {
      const put = http.expectOne('/api/v1/settings/current-user');
      writes.push(put.request.body.value);
      put.flush(null);
      await fixture.whenStable();
      http.expectOne('/api/v1/settings').flush([row({ userValue: snapshot })]);
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();

      expect(input().value).toBe('UTC');
    }

    const last = http.expectOne('/api/v1/settings/current-user');
    writes.push(last.request.body.value);
    last.flush(null);
    await fixture.whenStable();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'UTC' })]);
    await settleSave();

    expect(writes).toEqual(['UTC', 'Asia/Tokyo', 'UTC']);
    expect(input().value).toBe('UTC');
  });

  // 两次先后成功：前一次"已保存"的清理不能提前清掉后一次的提示，各自停留 2 秒
  it('keeps the latest saved hint for its own duration after an earlier save', async () => {
    const host = fixture.nativeElement as HTMLElement;
    const saved = () => host.querySelector('ng-icon[name="lucideCircleCheck"]');
    await typeAndSave('UTC');
    await flushSave();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'UTC' })]);
    await settleSave();
    await vi.advanceTimersByTimeAsync(500);

    await typeAndSave('Asia/Tokyo');
    await flushSave();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'Asia/Tokyo' })]);
    await settleSave();
    // 越过前一次提示的 2 秒（它在 400ms 出现），后一次的提示（900ms 起计）还在
    await vi.advanceTimersByTimeAsync(1150);
    fixture.detectChanges();
    expect(saved()).toBeTruthy();

    await vi.advanceTimersByTimeAsync(900);
    fixture.detectChanges();
    expect(saved()).toBeNull();
  });

  // 回车提交后光标还在输入框里，用户可以接着改；先前那次写入完成时清草稿不能抹掉新输入。
  it('keeps newer input when an earlier save completes', async () => {
    await typeAndSave('UTC');

    // 写入在途，用户接着改（还没提交）
    const field = input();
    field.value = 'Asia/Tokyo';
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    await flushSave();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'UTC' })]);
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    // 新输入必须还在：清草稿只该清掉"本次写入的那个值"
    expect(input().value).toBe('Asia/Tokyo');
  });

  // 草稿输入带空格、服务端存的是裁剪后的值：只有草稿真的被清掉，输入框才会显示
  // 服务端那份。两边都用同一个字符串的话，草稿留着也看不出来，这条用例会静默变绿。
  it('clears the draft only after the refreshed snapshot arrives', async () => {
    await typeAndSave('  UTC  ');

    const put = http.expectOne('/api/v1/settings/current-user');
    expect(put.request.body).toEqual({ name: 'Display.FreeTextProbe', value: 'UTC' });
    put.flush(null);
    await fixture.whenStable();

    http.expectOne('/api/v1/settings').flush([row({ userValue: 'UTC' })]);
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    // 草稿没清掉的话这里会是带空格的原始输入
    expect(input().value).toBe('UTC');
    expect(input().disabled).toBe(false);
  });

  // 失败就地报在那一行上，不弹 toast：反馈要落在用户刚碰的那个控件下面。
  it('reports a failed save inline on the row', async () => {
    await typeAndSave('UTC');
    http
      .expectOne('/api/v1/settings/current-user')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    await settleSave();

    const alert = (fixture.nativeElement as HTMLElement).querySelector('[role="alert"]');
    expect(alert).toBeTruthy();
    // 红叉之外必须带上原因：只有一个叉，用户既不知道是值不合法还是没权限，也就无从修正
    expect(alert?.querySelector('ng-icon[name="lucideCircleX"]')).toBeTruthy();
    expect(alert?.textContent?.trim().length).toBeGreaterThan(0);
  });

  // PUT 成功、刷新失败：库里已经是新值，界面不能退回旧值。
  it('keeps the typed value when the refresh after a successful save fails', async () => {
    await typeAndSave('UTC');
    await flushSave();
    http.expectOne('/api/v1/settings').flush('boom', { status: 500, statusText: 'Server Error' });
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(input().value).toBe('UTC');
    // 写入链路已结束，控件重新可用
    expect(input().disabled).toBe(false);
  });
});

/** 控件按真实响应形态判定：服务端省掉值为 null 的属性，非数值型设置不带 `minimum` / `maximum`。 */
describe('SettingSection input kinds', () => {
  async function render(settings: SettingOutputDto[]): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/settings').flush(settings);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const inputOf = (host: HTMLElement, name: string) =>
    host.querySelector(`[data-testid="setting-input-${name}"]`) as HTMLInputElement;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('renders a text box when the range fields are absent from the response', async () => {
    const text: SettingOutputDto = row({ name: 'Email.SmtpHost' });
    delete text.minimum;
    delete text.maximum;

    const host = await render([text]);

    expect(inputOf(host, 'Email.SmtpHost').type).toBe('text');
    expect(inputOf(host, 'Email.SmtpHost').getAttribute('inputmode')).toBeNull();
  });

  it('renders a number box with bounds when the setting has a range', async () => {
    const host = await render([row({ name: 'Email.SmtpPort', minimum: 1, maximum: 65535 })]);

    expect(inputOf(host, 'Email.SmtpPort').type).toBe('number');
    expect(inputOf(host, 'Email.SmtpPort').getAttribute('max')).toBe('65535');
  });
});

/** 首次加载失败与"没有可配置项"分开表达：说出原因、给重试，重试成功后渲染设置。 */
/** 设置行左右排：长连续文本在内容列内折行，控件留在列表内并按内容块垂直居中。 */
describe('SettingSection row layout', () => {
  const name = 'Display.FreeTextProbe';
  const longLabel =
    'ExternalLogin.Github.ClientSecretWithAVeryLongContinuousIdentifierThatHasNoSpaces';

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('wraps a long continuous label inside its column and centres the control on it', async () => {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    const host = fixture.nativeElement as HTMLElement;
    // 448px 正是输入类控件开始左右排的宽度，左列最窄
    host.style.display = 'block';
    host.style.width = '448px';
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/v1/settings')
      .flush([row({ name, displayName: longLabel })]);
    await fixture.whenStable();
    fixture.detectChanges();

    const group = host.querySelector('[data-slot="field-group"]')!.getBoundingClientRect();
    const content = host.querySelector('[data-slot="field-content"]')!.getBoundingClientRect();
    const input = host
      .querySelector(`[data-testid="setting-input-${name}"]`)!
      .getBoundingClientRect();
    const reset = host
      .querySelector(`[data-testid="setting-reset-${name}"]`)!
      .getBoundingClientRect();

    // 左右排、恢复按钮没有被挤出列表
    expect(input.left).toBeGreaterThan(content.right - 0.5);
    expect(reset.right).toBeLessThanOrEqual(group.right + 0.5);
    // 标签折成多行，控件按内容块的中线居中
    expect(content.height).toBeGreaterThan(input.height);
    expect(
      Math.abs(content.top + content.height / 2 - (input.top + input.height / 2)),
    ).toBeLessThan(1);
  });
});

describe('SettingSection load failure', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('shows the load failure with retry instead of the empty state, then renders after retry', async () => {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    const http = TestBed.inject(HttpTestingController);
    const host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    http
      .expectOne('/api/v1/settings')
      .flush({ detail: 'boom' }, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="settings-load-error"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="settings-empty"]')).toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="settings-load-error"] button')!.click();
    http.expectOne('/api/v1/settings').flush([row()]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="settings-load-error"]')).toBeNull();
    expect(hasRow(host, 'Display.FreeTextProbe')).toBe(true);
  });
});

/** 作用域由路由数据经组件输入给出；取错会把私人偏好写成所有人的默认值，因此按渲染出的作用域断言。 */
describe('SettingSection scope', () => {
  async function render(
    settings: SettingOutputDto[],
    inputs: Record<string, unknown> = {},
  ): Promise<ComponentFixture<SettingSection>> {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/settings').flush(settings);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  const scoped = [
    row({ name: 'Account.Only', allowsTenantScope: false }),
    row({ name: 'System.Only', allowsUserScope: false }),
  ];

  const panel = (fixture: ComponentFixture<SettingSection>) =>
    fixture.nativeElement.querySelector('[data-testid="settings-panel"]') as HTMLElement;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('lists only settings that allow a user override at account scope', async () => {
    const fixture = await render(scoped, { scope: 'account' });

    expect(panel(fixture).dataset['scope']).toBe('account');
    expect(hasRow(panel(fixture), 'Account.Only')).toBe(true);
    expect(hasRow(panel(fixture), 'System.Only')).toBe(false);
  });

  // 进程级设置（日志级别之类）两个层级标记都是 false：只按 allowsTenantScope 过滤
  // 会把它们全漏掉，界面上一项运维设置都看不到，而后端明明下发了。
  it('lists process-level settings at system scope as well', async () => {
    const fixture = await render(
      [
        row({
          name: 'Logging.MinimumLevel',
          allowsTenantScope: false,
          allowsUserScope: false,
          allowsHostScope: true,
        }),
      ],
      { scope: 'system' },
    );

    expect(hasRow(fixture.nativeElement as HTMLElement, 'Logging.MinimumLevel')).toBe(true);
  });

  it('lists only settings that allow a default value at system scope', async () => {
    const fixture = await render(scoped, { scope: 'system' });

    expect(panel(fixture).dataset['scope']).toBe('system');
    expect(hasRow(panel(fixture), 'System.Only')).toBe(true);
    expect(hasRow(panel(fixture), 'Account.Only')).toBe(false);
  });

  // 路由没写 scope 时按账户处理：宁可让人看到自己的偏好，也不要默认打开写全租户的那一页。
  it('falls back to account scope when no scope is declared', async () => {
    const fixture = await render(scoped);

    expect(panel(fixture).dataset['scope']).toBe('account');
  });
});

/** 分组由后端下发的分组标识驱动，前端不另列清单。 */
describe('SettingSection groups', () => {
  const grouped = [
    row({ name: 'Display.TimeZone', group: 'Display', groupDisplayName: '显示' }),
    row({ name: 'Display.Language', group: 'Display', groupDisplayName: '显示' }),
    row({ name: 'Logging.MinimumLevel', group: 'Operations', groupDisplayName: '运维' }),
    row({ name: 'Notify.Probe', group: 'NotificationPreferences', groupDisplayName: '通知' }),
  ];

  async function render(inputs: Record<string, unknown>): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/settings').flush(grouped);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const sectionsOf = (host: HTMLElement) =>
    [...host.querySelectorAll<HTMLElement>('[data-testid="settings-group-section"]')].map(
      (section) => section.dataset['group'],
    );

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('renders all groups in backend order with headings when no group is given', async () => {
    const host = await render({});

    expect(sectionsOf(host)).toEqual(['Display', 'Operations', 'NotificationPreferences']);
    expect([...host.querySelectorAll('h4')].map((h) => h.textContent?.trim())).toEqual([
      '显示',
      '运维',
      '通知',
    ]);
  });

  // 系统设置的每个面板就是一个分组：URL 里是短横线写法，单组时不重复出分组标题（面板标题已经有了）
  it('renders only the specified group without a group heading', async () => {
    const host = await render({ group: 'notification-preferences' });

    expect(sectionsOf(host)).toEqual(['NotificationPreferences']);
    expect(host.querySelector('h4')).toBeNull();
  });

  // 有专属面板的分组从通用面板里排除，免得同一项设置出现在两个面板上
  it('skips excluded groups', async () => {
    const host = await render({ exclude: ['NotificationPreferences'] });

    expect(sectionsOf(host)).toEqual(['Display', 'Operations']);
  });

  // 外观是设备偏好：只出现在个人偏好面板（个人作用域、不指定分组）的"显示"分组顶部
  it('shows appearance only at the top of the personal display group', async () => {
    const appearanceGroup = (host: HTMLElement) =>
      host
        .querySelector('[data-testid="appearance-setting"]')
        ?.closest<HTMLElement>('[data-testid="settings-group-section"]')?.dataset['group'];

    expect(appearanceGroup(await render({}))).toBe('Display');
  });

  it('leaves appearance out of system settings and single-group panels', async () => {
    expect(
      (await render({ scope: 'system' })).querySelector('[data-testid="appearance-setting"]'),
    ).toBeNull();
  });
});

/**
 * 进程级设置的生效范围：整组都是时由外壳在面板标题旁说明，分区里不再重复；混有其他层级时在那几行的标签旁标注。
 */
describe('SettingSection host scope hint', () => {
  const hostRow = (name: string, group: string) =>
    row({
      name,
      group,
      groupDisplayName: group,
      allowsTenantScope: false,
      allowsUserScope: false,
      allowsHostScope: true,
    });

  async function render(settings: SettingOutputDto[]): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    fixture.componentRef.setInput('scope', 'system');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/settings').flush(settings);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const sectionOf = (host: HTMLElement, group: string) =>
    host.querySelector<HTMLElement>(
      `[data-testid="settings-group-section"][data-group="${group}"]`,
    )!;
  const rowHints = (section: HTMLElement) =>
    section.querySelectorAll('[data-testid="setting-host-scope-hint"]').length;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('adds nothing inside the group when every setting in it is process-wide', async () => {
    const host = await render([
      hostRow('Logging.ProbeA', 'Operations'),
      hostRow('Logging.ProbeB', 'Operations'),
      row({ name: 'Display.Probe', group: 'Display', groupDisplayName: 'Display' }),
    ]);

    expect(rowHints(sectionOf(host, 'Operations'))).toBe(0);
    expect(rowHints(sectionOf(host, 'Display'))).toBe(0);
    expect(host.querySelector('[hlmBadge]')).toBeNull();
  });

  it('marks each process-wide row when the group mixes scopes', async () => {
    const host = await render([
      hostRow('Logging.ProbeA', 'Operations'),
      row({ name: 'Logging.TenantProbe', group: 'Operations', groupDisplayName: 'Operations' }),
      hostRow('Logging.ProbeB', 'Operations'),
    ]);

    expect(rowHints(sectionOf(host, 'Operations'))).toBe(2);
  });

  // 延迟只在刚改完时有意义：进程级的行保存成功时说明多久在所有服务器生效，其他层级只说已保存
  it('tells when a process-wide change reaches every server, only for process-wide rows', async () => {
    const settings = [
      hostRow('Logging.ProbeA', 'Operations'),
      row({ name: 'Logging.TenantProbe', group: 'Operations', groupDisplayName: 'Operations' }),
    ];
    TestBed.configureTestingModule({ imports: [SettingSection], providers: [...pageProviders()] });
    const fixture = TestBed.createComponent(SettingSection);
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('scope', 'system');
    fixture.detectChanges();
    http.expectOne('/api/v1/settings').flush(settings);
    await fixture.whenStable();
    fixture.detectChanges();

    const statusAfterReset = async (name: string) => {
      const host = fixture.nativeElement as HTMLElement;
      const reset = host.querySelector<HTMLButtonElement>(`[data-testid="setting-reset-${name}"]`)!;
      const field = reset.closest('hlm-field')!;
      reset.click();
      await fixture.whenStable();
      http.expectOne('/api/v1/settings/current-tenant').flush(null);
      await fixture.whenStable();
      http.expectOne('/api/v1/settings').flush(settings);
      await vi.waitFor(
        () => {
          fixture.detectChanges();
          // 等到成功的末态：保存中（saving / Saving…）不算
          expect(field.querySelector('[role="status"]')?.textContent?.trim()).toMatch(
            /^(settings\.saved|settings\.savedEverywhere|Saved.*)$/,
          );
        },
        { timeout: 3000 },
      );
      return field.querySelector('[role="status"]')!.textContent!.trim();
    };

    // 本地化形态的单测词条为空、文案回落成键名；不含本地化时是英文原文
    const everywhere = /savedEverywhere|every server/;
    expect(await statusAfterReset('Logging.ProbeA')).toMatch(everywhere);
    expect(await statusAfterReset('Logging.TenantProbe')).toMatch(/^(settings\.saved|Saved)$/);
  });
});

/** 开关没有占位符，本层未覆盖时显示继承来的生效值。 */
/**
 * 语言与时区：占位只写正在生效的值，值从哪层来写在标签后面的说明里（个人页继承到的是"跟随组织"，
 * 系统页继承到的是"部署默认"，用设备探测值时是"跟随系统"）。拼进一个占位时固定宽度放不下，
 * 被省略的正是值。系统页不显示管理员自己这台设备的探测值。
 */
describe('SettingSection display placeholders', () => {
  const timeZone = (overrides: Partial<SettingOutputDto>) =>
    row({
      name: 'Display.TimeZone',
      displayName: 'Time zone',
      group: 'Display',
      groupDisplayName: 'Display',
      defaultValue: null,
      ...overrides,
    });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function render(scope: string, setting: SettingOutputDto) {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    fixture.componentRef.setInput('scope', scope);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/settings').flush([setting]);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  /** 触发器上的文字、悬停提示，以及可聚焦按钮经 aria-describedby 读到的说明。 */
  function readRow(host: HTMLElement) {
    const button = host.querySelector<HTMLButtonElement>('button#Display\\.TimeZone')!;
    const describedBy = (button.getAttribute('aria-describedby') ?? '').split(/\s+/);
    return {
      text: button.textContent?.replace(/\s+/g, ' ').trim() ?? '',
      title: host.querySelector('hlm-combobox-trigger')?.getAttribute('title') ?? null,
      source: host.querySelector('[data-testid="setting-source"]')?.textContent?.trim() ?? '',
      description: describedBy
        .map((id) => (id ? document.getElementById(id)?.textContent?.trim() : ''))
        .join(' '),
    };
  }

  const ORGANIZATION = /Follows your organization|settings\.followOrganization/;
  const DEPLOYMENT = /Deployment default|settings\.deploymentDefault/;
  const SYSTEM = /Follows your system|settings\.followSystem/;

  it('shows the inherited organization value and names its source beside the label', async () => {
    const fixture = await render('account', timeZone({ tenantValue: 'Asia/Tokyo' }));
    const view = readRow(fixture.nativeElement);

    expect(view.text).toBe('Asia/Tokyo · UTC+9');
    expect(view.title).toBe('Asia/Tokyo · UTC+9');
    expect(view.source).toMatch(ORGANIZATION);
    // 来源在本行常驻描述里，读屏经控件的 aria-describedby 读到它
    expect(view.description).toMatch(ORGANIZATION);
  });

  it('names the organization when the account inherits the deployment baseline', async () => {
    const fixture = await render('account', timeZone({ defaultValue: 'Asia/Tokyo' }));
    const view = readRow(fixture.nativeElement);

    expect(view.text).toBe('Asia/Tokyo · UTC+9');
    expect(view.source).toMatch(ORGANIZATION);
  });

  it('shows the detected device value on the account page when nothing is set', async () => {
    const fixture = await render('account', timeZone({}));
    const view = readRow(fixture.nativeElement);

    expect(view.text.startsWith(`${Intl.DateTimeFormat().resolvedOptions().timeZone} · UTC`)).toBe(
      true,
    );
    expect(view.source).toMatch(SYSTEM);
  });

  it('names the deployment baseline on the system page', async () => {
    const fixture = await render('system', timeZone({ defaultValue: 'Asia/Tokyo' }));
    const view = readRow(fixture.nativeElement);

    expect(view.text).toBe('Asia/Tokyo · UTC+9');
    expect(view.source).toMatch(DEPLOYMENT);
  });

  it("does not present the admin's own device value as the organization's on the system page", async () => {
    const fixture = await render('system', timeZone({}));
    const view = readRow(fixture.nativeElement);

    // 占位本身就是"跟随系统"，说明里不再重复
    expect(view.text).toMatch(SYSTEM);
    expect(view.source).toBe('');
  });

  // 时区名后接此刻的偏移（巴黎随夏令时在 +1 与 +2 之间变）
  it('shows the zone name with its offset for an own value and drops the inherited source', async () => {
    const fixture = await render(
      'account',
      timeZone({ tenantValue: 'Asia/Tokyo', userValue: 'Europe/Paris' }),
    );
    const view = readRow(fixture.nativeElement);

    expect(view.text).toMatch(/^Europe\/Paris · UTC\+[12]$/);
    expect(view.title).toBe(view.text);
    expect(view.source).toBe('');
  });
});

/**
 * 写入状态与继承来源共用标签后面那一处：写入期间显示状态，状态结束后回到来源；
 * 清掉本层的值后来源重新出现。
 */
describe('SettingSection inherited source and save state', () => {
  afterEach(() => {
    vi.useRealTimers();
    TestBed.inject(HttpTestingController).verify();
  });

  it('replaces the source with the save state and brings it back afterwards', async () => {
    const timeZone = (userValue: string | null) =>
      row({
        name: 'Display.TimeZone',
        group: 'Display',
        defaultValue: null,
        tenantValue: 'Asia/Tokyo',
        userValue,
      });
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne('/api/v1/settings').flush([timeZone('Europe/Paris')]);
    await fixture.whenStable();
    fixture.detectChanges();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    const host = fixture.nativeElement as HTMLElement;
    const source = () => host.querySelector('[data-testid="setting-source"]');

    // 本层有值：不写来源
    expect(source()).toBeNull();

    (host.querySelector('[data-testid="setting-reset-Display.TimeZone"]') as HTMLElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(host.querySelector('hlm-spinner')).toBeTruthy();
    expect(source()).toBeNull();

    http.expectOne('/api/v1/settings/current-user').flush(null);
    await fixture.whenStable();
    http.expectOne('/api/v1/settings').flush([timeZone(null)]);
    await vi.advanceTimersByTimeAsync(400);
    fixture.detectChanges();
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeTruthy();
    expect(source()).toBeNull();

    await vi.advanceTimersByTimeAsync(2000);
    fixture.detectChanges();
    expect(host.querySelector('ng-icon[name="lucideCircleCheck"]')).toBeNull();
    expect(source()?.textContent).toMatch(/Follows your organization|settings\.followOrganization/);
  });
});

/**
 * 长值在设置页的固定宽度（`w-64`）里单行省略，箭头留在控件内，悬停提示给出全文。省略由 `styles.css`
 * 按 data-slot 统一处理（上游选择框的值区是 flex，省略号不生效；组合框的值区没有收缩约束），
 * 所以这组用例量的是全局样式加载后的真实几何。
 */
describe('SettingSection long values', () => {
  const LONG = 'America/Argentina/Rio_Gallegos/With_A_Name_Longer_Than_Any_Zone';

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function render(settings: SettingOutputDto[]): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    fixture.componentRef.setInput('scope', 'account');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/settings').flush(settings);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const timeZone = (overrides: Partial<SettingOutputDto>) =>
    row({ name: 'Display.TimeZone', group: 'Display', defaultValue: null, ...overrides });
  const logLevel = (overrides: Partial<SettingOutputDto>) =>
    row({ name: 'Logging.MinimumLevel', group: 'Logging', defaultValue: null, ...overrides });

  /** 值区单行省略、确实溢出，且右缘不压箭头，箭头在控件内。 */
  function expectTruncatedInside(button: HTMLElement, slot: string) {
    const value = button.querySelector<HTMLElement>(`[data-slot="${slot}"]`)!;
    const icon = button.querySelector('ng-icon')!.getBoundingClientRect();
    const box = button.getBoundingClientRect();
    // 宽列表下控件固定 w-64：没进这一档就测不到缺陷
    expect(box.width).toBeCloseTo(256, 0);
    const style = getComputedStyle(value);
    expect(style.display).toBe('block');
    expect(style.textOverflow).toBe('ellipsis');
    expect(value.scrollWidth).toBeGreaterThan(value.clientWidth);
    expect(value.getBoundingClientRect().right).toBeLessThanOrEqual(icon.left + 0.5);
    expect(icon.right).toBeLessThanOrEqual(box.right + 0.5);
  }

  /** 候选里最长的时区名：设置页固定宽度里放不下，偏移得照样完整可见。 */
  const LONG_ZONE = 'America/Argentina/Rio_Gallegos';

  /** 时区只省略名字：名字溢出且带省略号，偏移完整、不压箭头，箭头在控件内。 */
  function expectZoneNameTruncated(button: HTMLElement) {
    const [name, offset] = Array.from(
      button.querySelectorAll<HTMLElement>('[data-testid="time-zone-value"] > span'),
    );
    const icon = button.querySelector('ng-icon')!.getBoundingClientRect();
    const box = button.getBoundingClientRect();
    expect(box.width).toBeCloseTo(256, 0);
    expect(getComputedStyle(name).textOverflow).toBe('ellipsis');
    expect(name.scrollWidth).toBeGreaterThan(name.clientWidth);
    expect(offset.textContent?.trim()).toMatch(/^· UTC[+-]\d+$/);
    expect(offset.scrollWidth).toBeLessThanOrEqual(offset.clientWidth);
    expect(offset.getBoundingClientRect().right).toBeLessThanOrEqual(icon.left + 0.5);
    expect(icon.right).toBeLessThanOrEqual(box.right + 0.5);
  }

  it('truncates only the name of a long inherited time zone and keeps its offset', async () => {
    const host = await render([timeZone({ tenantValue: LONG_ZONE })]);
    const button = host.querySelector<HTMLElement>('button#Display\\.TimeZone')!;

    expectZoneNameTruncated(button);
    expect(host.querySelector('hlm-combobox-trigger')!.getAttribute('title')).toBe(
      'America/Argentina/Rio_Gallegos · UTC-3',
    );
  });

  it('truncates only the name of a long own time zone and keeps its offset', async () => {
    const host = await render([timeZone({ userValue: LONG_ZONE })]);
    const button = host.querySelector<HTMLElement>('button#Display\\.TimeZone')!;

    expectZoneNameTruncated(button);
  });

  it('truncates a long value inside the select with an ellipsis', async () => {
    const host = await render([logLevel({ userValue: LONG })]);
    const button = host.querySelector<HTMLElement>('button#Logging\\.MinimumLevel')!;

    expectTruncatedInside(button, 'select-value');
    expect(host.querySelector('hlm-select-trigger')!.getAttribute('title')).toBe(LONG);
  });

  it('truncates a long inherited placeholder inside the select', async () => {
    const host = await render([logLevel({ tenantValue: LONG })]);
    const button = host.querySelector<HTMLElement>('button#Logging\\.MinimumLevel')!;

    expectTruncatedInside(button, 'select-value');
  });

  // 对照：短值不受影响；无值且无占位时值区仍按上游隐藏，没被统一规则改成块级
  it('leaves short values whole and keeps an empty value hidden', async () => {
    const host = await render([
      timeZone({ userValue: 'Asia/Tokyo' }),
      logLevel({ userValue: null, tenantValue: null }),
    ]);
    const zone = host.querySelector<HTMLElement>(
      'button#Display\\.TimeZone [data-testid="time-zone-value"] > span',
    )!;
    const level = host.querySelector<HTMLElement>('button#Logging\\.MinimumLevel')!;

    expect(zone.getBoundingClientRect().width).toBeGreaterThan(0);
    expect(zone.scrollWidth).toBeLessThanOrEqual(zone.clientWidth);
    const empty = level.querySelector<HTMLElement>('hlm-select-value')!;
    expect(empty.hasAttribute('data-hidden')).toBe(true);
    expect(getComputedStyle(empty).display).toBe('none');
  });

  // 触发器上放不下的时区名，展开后在候选列表里能看到全名：城市名换行而不截断
  it('shows every candidate city in full when the list opens', async () => {
    const host = await render([timeZone({})]);
    host.querySelector<HTMLElement>('button#Display\\.TimeZone')!.click();
    await TestBed.inject(ApplicationRef).whenStable();

    const cities = Array.from(
      document.querySelectorAll<HTMLElement>('[data-slot="combobox-item"] > span:first-child'),
    );
    expect(cities.length).toBeGreaterThan(100);
    const clipped = cities.filter((city) => {
      const width = city.getBoundingClientRect().width;
      return width === 0 || city.scrollWidth > city.clientWidth;
    });
    expect(clipped.map((city) => city.textContent?.trim())).toEqual([]);
  });
});

/** 展开时区列表即定位到当前值：几百项里停在顶部就看不到它。 */
describe('SettingSection time zone list position', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function open(setting: Partial<SettingOutputDto>) {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    fixture.componentRef.setInput('scope', 'account');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/v1/settings')
      .flush([row({ name: 'Display.TimeZone', group: 'Display', defaultValue: null, ...setting })]);
    await fixture.whenStable();
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLElement>('button#Display\\.TimeZone')!
      .click();
    await TestBed.inject(ApplicationRef).whenStable();
    // 定位在展开后的下一帧完成
    await new Promise((resolve) => requestAnimationFrame(resolve));

    const list = document.querySelector<HTMLElement>('[data-slot="combobox-list"]')!;
    const option = list.querySelector<HTMLElement>(
      '[role="option"][data-value="Pacific/Auckland"]',
    )!;
    const listBox = list.getBoundingClientRect();
    const optionBox = option.getBoundingClientRect();
    return {
      // 选中项完整落在列表可视区内，且列表确实滚动过（它排在很后面）
      visible: optionBox.top >= listBox.top - 0.5 && optionBox.bottom <= listBox.bottom + 0.5,
      scrolled: list.scrollTop > 0,
      highlighted: option.hasAttribute('data-highlighted'),
      selected: option.getAttribute('aria-selected'),
    };
  }

  it('scrolls to and highlights the own value', async () => {
    const view = await open({ userValue: 'Pacific/Auckland' });

    expect(view).toEqual({ visible: true, scrolled: true, highlighted: true, selected: 'true' });
  });

  // 继承来的值只是占位：定位并高亮，但不标成已选
  it('scrolls to the inherited value without marking it selected', async () => {
    const view = await open({ tenantValue: 'Pacific/Auckland' });

    expect(view).toEqual({ visible: true, scrolled: true, highlighted: true, selected: 'false' });
  });
});

describe('SettingSection switches', () => {
  const switches = [
    row({ name: 'Probe.DefaultOn', isBoolean: true, defaultValue: 'true' }),
    row({ name: 'Probe.TenantOff', isBoolean: true, defaultValue: 'true', tenantValue: 'false' }),
    row({ name: 'Probe.UserOff', isBoolean: true, defaultValue: 'true', userValue: 'false' }),
  ];

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function render(scope: string): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    const fixture = TestBed.createComponent(SettingSection);
    fixture.componentRef.setInput('scope', scope);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/settings').flush(switches);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  async function checkedOf(scope: string): Promise<Record<string, string | null>> {
    const host = await render(scope);
    return Object.fromEntries(
      switches.map((setting) => [
        setting.name,
        host
          .querySelector(`[data-testid="setting-switch-${setting.name}"] [role="switch"]`)
          ?.getAttribute('aria-checked') ?? null,
      ]),
    );
  }

  it('shows the inherited system or code default when the account has no override', async () => {
    expect(await checkedOf('account')).toEqual({
      'Probe.DefaultOn': 'true',
      'Probe.TenantOff': 'false',
      'Probe.UserOff': 'false',
    });
  });

  // 开关没有宽度，标签、开关与恢复按钮排在同一行；开关不会自动关联所在字段的描述，要显式指向本行的描述
  it('keeps the label, switch and reset on one line and describes the switch by its own row', async () => {
    const host = await render('account');

    for (const setting of switches) {
      const control = host.querySelector<HTMLElement>(
        `[data-testid="setting-switch-${setting.name}"] [role="switch"]`,
      )!;
      const field = control.closest<HTMLElement>('[data-slot="field"]')!;
      const label = field.querySelector<HTMLLabelElement>(`label[for="${setting.name}"]`)!;
      const controls = control.closest('[data-slot="field"] > div')!;
      expect(label.textContent?.trim()).toBe(setting.displayName);
      expect(
        controls.querySelector(`[data-testid="setting-reset-${setting.name}"]`),
      ).not.toBeNull();

      // 标签与开关在同一水平线上：中线对齐
      const middle = (el: Element) => {
        const box = el.getBoundingClientRect();
        return box.top + box.height / 2;
      };
      expect(Math.abs(middle(label.parentElement!) - middle(control))).toBeLessThan(1);

      const description = host.querySelector(`[id="${control.getAttribute('aria-describedby')}"]`);
      expect(description?.getAttribute('data-slot')).toBe('field-description');
      expect(description?.closest('[data-slot="field"]')).toBe(field);
    }
  });

  // 开关扩出的命中区不能被裁，也不能与同一行的恢复按钮互相覆盖
  it('keeps full touch targets for the switch and its reset button on coarse pointers', async () => {
    await setCoarsePointer(true);
    onTestFinished(() => setCoarsePointer(false));
    const host = await render('account');
    const name = 'Probe.DefaultOn';
    const control = host.querySelector<HTMLElement>(
      `[data-testid="setting-switch-${name}"] [role="switch"]`,
    )!;

    expectTouchTarget(control);
    expectTouchTarget(host.querySelector(`[data-testid="setting-reset-${name}"]`)!);

    // 点在开关视觉边界之外、扩出的命中区之内：切换的是开关，不是旁边的恢复按钮
    await tapAt(control, 20, 0);
    const write = TestBed.inject(HttpTestingController).expectOne('/api/v1/settings/current-user');
    expect(write.request.method).toBe('PUT');
    expect(write.request.body).toEqual({ name, value: 'false' });
  });

  // 拨两下：第一次写完刷新的快照（关）不能把开关拨回去，开关保持最新意图（开），两次按序写入
  it('keeps the latest toggle while an earlier write of the same switch completes', async () => {
    const host = await render('account');
    const http = TestBed.inject(HttpTestingController);
    const name = 'Probe.DefaultOn';
    const control = () =>
      host.querySelector<HTMLElement>(`[data-testid="setting-switch-${name}"] [role="switch"]`)!;
    const withUserValue = (userValue: string) =>
      switches.map((setting) => (setting.name === name ? { ...setting, userValue } : setting));

    control().click();
    await TestBed.inject(ApplicationRef).whenStable();
    control().click();
    await TestBed.inject(ApplicationRef).whenStable();

    const first = http.expectOne('/api/v1/settings/current-user');
    expect(first.request.body).toEqual({ name, value: 'false' });
    first.flush(null);
    await TestBed.inject(ApplicationRef).whenStable();
    const refreshFirst = await vi.waitFor(() => http.expectOne('/api/v1/settings'));
    refreshFirst.flush(withUserValue('false'));
    await TestBed.inject(ApplicationRef).whenStable();
    expect(control().getAttribute('aria-checked')).toBe('true');

    // 第二次写入排在第一条链之后，链收尾要过几轮微任务才发出
    const second = await vi.waitFor(() => http.expectOne('/api/v1/settings/current-user'));
    expect(second.request.body).toEqual({ name, value: 'true' });
    second.flush(null);
    await TestBed.inject(ApplicationRef).whenStable();
    const refreshSecond = await vi.waitFor(() => http.expectOne('/api/v1/settings'));
    refreshSecond.flush(withUserValue('true'));
    await TestBed.inject(ApplicationRef).whenStable();
    expect(control().getAttribute('aria-checked')).toBe('true');
  });

  it('inherits only the code default at system scope', async () => {
    expect(await checkedOf('system')).toEqual({
      'Probe.DefaultOn': 'true',
      'Probe.TenantOff': 'false',
      'Probe.UserOff': 'true',
    });
  });
});
//#if (ExternalLogin)

/**
 * 外部登录按提供方分卡片，卡片里的行与其他分组同一份实现；本组设置保存或恢复默认后重取状态，
 * 其他分组的写入不触发。
 */
describe('SettingSection external login', () => {
  const statusUrl = '/api/v1/settings/external-login/status';
  const hostRow = (name: string, group: string, overrides: Partial<SettingOutputDto> = {}) =>
    row({
      name,
      group,
      allowsTenantScope: false,
      allowsUserScope: false,
      allowsHostScope: true,
      ...overrides,
    });

  const settings = () => [
    hostRow('ExternalLogin.Github.ClientId', 'ExternalLogin'),
    hostRow('ExternalLogin.Google.Enabled', 'ExternalLogin', { isBoolean: true }),
    hostRow('ExternalLogin.Github.Enabled', 'ExternalLogin', { isBoolean: true }),
    // 不符合 `ExternalLogin.{提供方}.{字段}` 约定：不进卡片，也不能从界面上消失
    hostRow('ExternalLogin.CallbackPath', 'ExternalLogin'),
    hostRow('Logging.Probe', 'Operations', { isBoolean: true }),
  ];

  let fixture: ComponentFixture<SettingSection>;
  let http: HttpTestingController;

  async function render(scope: string): Promise<HTMLElement> {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      providers: [...pageProviders()],
    });
    fixture = TestBed.createComponent(SettingSection);
    http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('scope', scope);
    fixture.detectChanges();
    http.expectOne('/api/v1/settings').flush(settings());
    await fixture.whenStable();
    fixture.detectChanges();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    return fixture.nativeElement as HTMLElement;
  }

  /** 卡片里渲染了哪些设置行，按重置按钮判定（同 {@link hasRow}）。 */
  function cardRows(host: HTMLElement, provider: string): string[] {
    const card = host.querySelector(
      `[data-testid="external-login-provider"][data-provider="${provider}"]`,
    );
    return [...(card?.querySelectorAll<HTMLElement>('[data-testid^="setting-reset-"]') ?? [])].map(
      (button) => button.dataset['testid']!.replace('setting-reset-', ''),
    );
  }

  /** 点该行的"恢复默认"，应答 PUT 与随后的快照刷新，并走完"保存中"的最短时长，下一次写入才会出队。 */
  async function reset(host: HTMLElement, name: string): Promise<void> {
    host.querySelector<HTMLButtonElement>(`[data-testid="setting-reset-${name}"]`)!.click();
    await fixture.whenStable();
    http.expectOne('/api/v1/settings/current-tenant').flush(null);
    await fixture.whenStable();
    fixture.detectChanges();
    http.expectOne('/api/v1/settings').flush(settings());
    await vi.advanceTimersByTimeAsync(400);
    await fixture.whenStable();
  }

  afterEach(() => {
    try {
      http.verify();
    } finally {
      try {
        fixture.destroy();
      } finally {
        vi.clearAllTimers();
        vi.useRealTimers();
      }
    }
  });

  it('puts each provider in its own card, switch first, and keeps unmatched settings as rows', async () => {
    const host = await render('system');
    http.expectOne(statusUrl).flush({ providers: [] });

    expect(cardRows(host, 'github')).toEqual([
      'ExternalLogin.Github.Enabled',
      'ExternalLogin.Github.ClientId',
    ]);
    expect(cardRows(host, 'google')).toEqual(['ExternalLogin.Google.Enabled']);
    // 约定外的设置在卡片之外按普通行渲染
    expect(hasRow(host, 'ExternalLogin.CallbackPath')).toBe(true);
    expect(
      host
        .querySelector('[data-testid="setting-reset-ExternalLogin.CallbackPath"]')
        ?.closest('[data-testid="external-login-provider"]'),
    ).toBeNull();
    // 卡片里的行是同一份行实现：开关照样渲染成开关，并关联到本卡片里本行的描述
    const cardSwitch = host.querySelector(
      '[data-testid="setting-switch-ExternalLogin.Github.Enabled"] [role="switch"]',
    )!;
    const description = host.querySelector(`[id="${cardSwitch.getAttribute('aria-describedby')}"]`);
    expect(
      description
        ?.closest('[data-testid="external-login-provider"]')
        ?.getAttribute('data-provider'),
    ).toBe('github');
  });

  it('shows the saving state on a row inside a card', async () => {
    const host = await render('system');
    http.expectOne(statusUrl).flush({ providers: [] });

    const card = host.querySelector(
      '[data-testid="external-login-provider"][data-provider="github"]',
    )!;
    const resetButton = card.querySelector<HTMLButtonElement>(
      '[data-testid="setting-reset-ExternalLogin.Github.ClientId"]',
    )!;
    resetButton.click();
    await fixture.whenStable();
    fixture.detectChanges();

    // 行状态由设置区块持有，模板插在卡片里照样跟着刷新
    expect(card.querySelector('hlm-field-description hlm-spinner')).not.toBeNull();

    http.expectOne('/api/v1/settings/current-tenant').flush(null);
    await fixture.whenStable();
    http.expectOne('/api/v1/settings').flush(settings());
    await vi.advanceTimersByTimeAsync(400);
    await fixture.whenStable();
    http.expectOne(statusUrl).flush({ providers: [] });
  });

  it('refetches the status after a setting in the group is saved, not after other groups', async () => {
    const host = await render('system');
    expect(host.querySelector('app-external-login-providers')).not.toBeNull();
    http.expectOne(statusUrl).flush({ providers: [] });

    await reset(host, 'Logging.Probe');
    fixture.detectChanges();
    http.expectNone(statusUrl);

    await reset(host, 'ExternalLogin.Github.Enabled');
    fixture.detectChanges();
    http.expectOne(statusUrl).flush({ providers: [] });

    // 约定外的行也属于本组：写入同样触发重取
    await reset(host, 'ExternalLogin.CallbackPath');
    fixture.detectChanges();
    http.expectOne(statusUrl).flush({ providers: [] });
  });

  it('does not mount the provider cards on personal settings', async () => {
    const host = await render('account');

    expect(host.querySelector('app-external-login-providers')).toBeNull();
    http.expectNone(statusUrl);
  });
});
//#endif
//#if (IncludeLocalization)

/**
 * 行内文案经模板结构指令的 t 取得。这张表单没有随语言变化的校验信号，
 * 切换语言后能换成新语言只靠结构指令重绘——文案改回组件里的 translate() 调用这里就会红。
 */
describe('SettingSection language', () => {
  it('re-renders row labels when the language changes', async () => {
    TestBed.configureTestingModule({
      imports: [SettingSection],
      // 需要第二种语言可切换；装在公共提供者之后，覆盖其中只有英文的那份配置
      providers: [...pageProviders(), ...provideTranslocoTesting(['en', 'zh-CN'])],
    });
    const fixture = TestBed.createComponent(SettingSection);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    // 可选语言变多后，语言服务按浏览器语言激活时会让设置再取一次；这里只关心文案
    http.match('/api/v1/settings').forEach((request) => request.flush([row()]));
    await fixture.whenStable();
    http.match('/api/v1/settings').forEach((request) => request.flush([row()]));
    fixture.detectChanges();

    const transloco = TestBed.inject(TranslocoService);
    transloco.setTranslation({ settings: { reset: '恢复默认' } }, 'zh-CN');
    const reset = () =>
      (fixture.nativeElement as HTMLElement).querySelector(
        '[data-testid="setting-reset-Display.FreeTextProbe"]',
      )!;
    // 切换语言会让页面按新语言重取显示名：应答它，页面才算稳定。界面语言由设置派生（读取会重新应用），
    // 真实切换器先把选择写进个人设置，这里的应答同样带上它
    const language = (lang: string) =>
      row({ name: 'Display.Language', group: 'Display', userValue: lang });
    const switchTo = async (lang: string) => {
      transloco.setActiveLang(lang);
      await new Promise((resolve) => setTimeout(resolve));
      http.match('/api/v1/settings').forEach((request) => request.flush([row(), language(lang)]));
      await fixture.whenStable();
    };
    await switchTo('en');
    expect(reset().getAttribute('aria-label')).toBe('settings.reset');

    await switchTo('zh-CN');

    expect(reset().getAttribute('aria-label')).toBe('恢复默认');
  });
});
//#endif
