import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
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
   * 离散控件在本行写入期间禁用、写完恢复；以每行都有的重置按钮断言，用虚拟时间收尾，
   * 确认禁用态不会一直挂着。
   */
  it('disables the discrete controls on that row while it saves', async () => {
    const resetButton = () =>
      (fixture.nativeElement as HTMLElement).querySelector(
        '[data-testid="setting-reset-Display.FreeTextProbe"]',
      ) as HTMLButtonElement;

    await typeAndSave('UTC');
    expect(resetButton().disabled).toBe(true);

    await flushSave();
    http.expectOne('/api/v1/settings').flush([row({ userValue: 'UTC' })]);
    await settleSave();

    expect(resetButton().disabled).toBe(false);
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
});

/** 进程级设置的生效范围：整组都是时在分组顶部说一次，混有其他层级时按行标注。 */
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
  const groupHints = (section: HTMLElement) =>
    section.querySelectorAll('[data-testid="settings-group-host-scope-hint"]').length;
  const rowHints = (section: HTMLElement) =>
    section.querySelectorAll('[data-testid="setting-host-scope-hint"]').length;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('says it once under the group when every setting in the group is process-wide', async () => {
    const host = await render([
      hostRow('Logging.ProbeA', 'Operations'),
      hostRow('Logging.ProbeB', 'Operations'),
      row({ name: 'Display.Probe', group: 'Display', groupDisplayName: 'Display' }),
    ]);

    const operations = sectionOf(host, 'Operations');
    expect(groupHints(operations)).toBe(1);
    expect(rowHints(operations)).toBe(0);
    // 没有进程级设置的分组两处都不出
    expect(groupHints(sectionOf(host, 'Display'))).toBe(0);
    expect(rowHints(sectionOf(host, 'Display'))).toBe(0);
  });

  it('marks each process-wide row when the group mixes scopes', async () => {
    const host = await render([
      hostRow('Logging.ProbeA', 'Operations'),
      row({ name: 'Logging.TenantProbe', group: 'Operations', groupDisplayName: 'Operations' }),
      hostRow('Logging.ProbeB', 'Operations'),
    ]);

    const operations = sectionOf(host, 'Operations');
    expect(groupHints(operations)).toBe(0);
    expect(rowHints(operations)).toBe(2);
  });

  // 单组面板没有分组标题，说明照样出在分组顶部
  it('keeps the group hint on a single-group panel without a heading', async () => {
    const host = await render([hostRow('Logging.ProbeA', 'Operations')]);

    expect(host.querySelector('h4')).toBeNull();
    expect(groupHints(sectionOf(host, 'Operations'))).toBe(1);
    expect(rowHints(sectionOf(host, 'Operations'))).toBe(0);
  });
});

/** 开关没有占位符，本层未覆盖时显示继承来的生效值。 */
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
    expect(resetButton.disabled).toBe(true);
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
    transloco.setActiveLang('en');
    await fixture.whenStable();
    expect(reset().getAttribute('aria-label')).toBe('settings.reset');

    transloco.setActiveLang('zh-CN');
    await fixture.whenStable();

    expect(reset().getAttribute('aria-label')).toBe('恢复默认');
  });
});
//#endif
