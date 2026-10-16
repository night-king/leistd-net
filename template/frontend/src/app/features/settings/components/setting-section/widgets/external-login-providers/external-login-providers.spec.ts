import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, Subject, throwError } from 'rxjs';

// prettier-ignore
import {
  ExternalLoginProviders,
  ExternalLoginProviderSettings,
  partitionExternalLoginSettings,
} from './external-login-providers';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { SettingService } from '../../../../../../core/settings/setting-service';
// prettier-ignore
import {
  ExternalLoginProviderStatusOutputDto,
  ExternalLoginStatusOutputDto,
  SettingOutputDto,
} from '../../../../../../core/settings/setting.dto';

import type { Mock } from 'vitest';

function setting(name: string): SettingOutputDto {
  return {
    name,
    displayName: name,
    group: 'ExternalLogin',
    groupDisplayName: '外部登录',
    userValue: null,
    tenantValue: null,
    defaultValue: null,
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    canManage: true,
  };
}

function provider(
  overrides: Partial<ExternalLoginProviderStatusOutputDto> = {},
): ExternalLoginProviderStatusOutputDto {
  return {
    provider: 'github',
    enabled: false,
    hasClientId: false,
    hasClientSecret: false,
    active: false,
    callbackUrl: 'https://id.example.test/api/v1/external-auth/github/signin',
    ...overrides,
  };
}

const names = (settings: readonly SettingOutputDto[]) => settings.map((item) => item.name);

/** 设置名是跨前后端的字符串契约：按它分卡片，约定外的设置不能静默消失。 */
describe('partitionExternalLoginSettings', () => {
  it('groups by provider in backend order and orders the switch before the credentials', () => {
    const { providers, others } = partitionExternalLoginSettings([
      setting('ExternalLogin.Github.ClientSecret'),
      setting('ExternalLogin.Google.Enabled'),
      setting('ExternalLogin.Github.Scope'),
      setting('ExternalLogin.Github.ClientId'),
      setting('ExternalLogin.Github.Enabled'),
      setting('ExternalLogin.Google.ClientId'),
    ]);

    expect(providers.map((item) => item.provider)).toEqual(['Github', 'Google']);
    // 约定外的字段排在已知字段之后
    expect(names(providers[0].settings)).toEqual([
      'ExternalLogin.Github.Enabled',
      'ExternalLogin.Github.ClientId',
      'ExternalLogin.Github.ClientSecret',
      'ExternalLogin.Github.Scope',
    ]);
    expect(names(providers[1].settings)).toEqual([
      'ExternalLogin.Google.Enabled',
      'ExternalLogin.Google.ClientId',
    ]);
    expect(others).toEqual([]);
  });

  it.each([
    'ExternalLogin.CallbackPath',
    'ExternalLogin.Github.Options.Scope',
    'Other.Github.Enabled',
  ])('keeps %s out of the provider cards', (name) => {
    const { providers, others } = partitionExternalLoginSettings([
      setting('ExternalLogin.Github.Enabled'),
      setting(name),
    ]);

    expect(names(others)).toEqual([name]);
    expect(names(providers.flatMap((item) => item.settings))).toEqual([
      'ExternalLogin.Github.Enabled',
    ]);
  });
});

/** 行模板由宿主给出（真实页面里是设置区块的行），这里只渲染设置名，验卡片放了哪些行。 */
@Component({
  imports: [ExternalLoginProviders],
  template: `
    <ng-template #row let-setting>
      <span data-testid="row">{{ setting.name }}</span>
    </ng-template>
    <app-external-login-providers
      [providers]="providers()"
      [rowTemplate]="row"
      [revision]="revision()"
    />
  `,
})
class TestHost {
  readonly providers = signal<ExternalLoginProviderSettings[]>(
    partitionExternalLoginSettings([
      setting('ExternalLogin.Github.Enabled'),
      setting('ExternalLogin.Github.ClientId'),
      setting('ExternalLogin.Google.Enabled'),
    ]).providers,
  );
  readonly revision = signal(0);
}

/**
 * 每个提供方一张卡片：标题旁是本实例上的生效状态，开关打开了却没生效时当场说出缺什么；
 * 机密不回显，部署提供的密钥只能靠这里看出来。
 */
describe('ExternalLoginProviders', () => {
  let fixture: ComponentFixture<TestHost>;
  let getStatus: Mock<() => Observable<ExternalLoginStatusOutputDto>>;
  let writeText: Mock;

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function cardOf(name: string): HTMLElement {
    const card = host().querySelector<HTMLElement>(
      `[data-testid="external-login-provider"][data-provider="${name}"]`,
    );
    if (!card) throw new Error(`${name} has no card`);
    return card;
  }

  function rowsOf(name: string): string[] {
    return [...cardOf(name).querySelectorAll('[data-testid="row"]')].map(
      (row) => row.textContent?.trim() ?? '',
    );
  }

  function stateOf(name: string): string | null {
    return (
      cardOf(name).querySelector('[data-testid="external-login-state"]')?.textContent?.trim() ??
      null
    );
  }

  function hintOf(name: string): string | null {
    return (
      cardOf(name).querySelector('[data-testid="external-login-hint"]')?.textContent?.trim() ?? null
    );
  }

  function callbackOf(name: string): string | null {
    return (
      cardOf(name).querySelector('[data-testid="external-login-callback-url"]')?.textContent ?? null
    );
  }

  async function render(
    status: ExternalLoginStatusOutputDto | Observable<ExternalLoginStatusOutputDto>,
  ): Promise<void> {
    getStatus.mockReturnValue(status instanceof Observable ? status : of(status));
    fixture = TestBed.createComponent(TestHost);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** 词条键在本地化形态下原样显示（测试装配不加载词条），不含本地化时是英文表里的句子。 */
  function text(key: string, english: string): string {
    //#if (IncludeLocalization)
    void english;
    return `settings.externalLoginProviders.${key}`;
    //#else
    void key;
    return english;
    //#endif
  }

  beforeEach(() => {
    getStatus = vi.fn().mockName('SettingService.getExternalLoginStatus');
    writeText = vi.fn().mockName('writeText').mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    TestBed.configureTestingModule({
      imports: [TestHost],
      // prettier-ignore
      providers: [
        { provide: SettingService, useValue: { getExternalLoginStatus: getStatus } },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
  });

  afterEach(() => fixture.destroy());

  it('renders one card per provider with its own rows, labelled by display name', async () => {
    await render({ providers: [] });

    expect(
      [...host().querySelectorAll<HTMLElement>('[data-testid="external-login-provider"]')].map(
        (card) => card.querySelector('h4')?.textContent?.trim(),
      ),
    ).toEqual(['GitHub', 'Google']);
    expect(rowsOf('github')).toEqual([
      'ExternalLogin.Github.Enabled',
      'ExternalLogin.Github.ClientId',
    ]);
    expect(rowsOf('google')).toEqual(['ExternalLogin.Google.Enabled']);
  });

  // 新增的提供方前端没登记展示名时退回设置名里的原文，不能显示成空标题
  it('falls back to the setting name segment for an unknown provider', async () => {
    await render({ providers: [] });
    fixture.componentInstance.providers.set(
      partitionExternalLoginSettings([setting('ExternalLogin.Gitee.Enabled')]).providers,
    );
    await settle();

    expect(cardOf('gitee').querySelector('h4')?.textContent?.trim()).toBe('Gitee');
    expect(rowsOf('gitee')).toEqual(['ExternalLogin.Gitee.Enabled']);
  });

  it('shows the rows with a spinner but no state while loading', async () => {
    await render(new Subject<ExternalLoginStatusOutputDto>());

    expect(rowsOf('github')).toHaveLength(2);
    expect(cardOf('github').querySelector('hlm-spinner')).not.toBeNull();
    expect(stateOf('github')).toBeNull();
    expect(cardOf('github').querySelector('[data-testid="external-login-credentials"]')).toBeNull();
    expect(host().querySelector('[data-testid="external-login-status-error"]')).toBeNull();
  });

  // 状态读不到不影响改设置：错误在卡片上方报一次，卡片照常渲染设置行
  it('reports a load failure once, keeps the rows, and recovers on retry', async () => {
    await render(throwError(() => new Error('The status service is unavailable.')));

    const errors = host().querySelectorAll('[data-testid="external-login-status-error"]');
    expect(errors).toHaveLength(1);
    expect(errors[0].textContent).toContain('The status service is unavailable.');
    expect(rowsOf('github')).toHaveLength(2);
    expect(rowsOf('google')).toHaveLength(1);
    expect(stateOf('github')).toBeNull();
    expect(callbackOf('github')).toBeNull();
    expect(cardOf('github').querySelector('[data-testid="external-login-credentials"]')).toBeNull();

    getStatus.mockReturnValue(of({ providers: [provider()] }));
    host().querySelector<HTMLButtonElement>('[data-testid="external-login-status-retry"]')!.click();
    await settle();

    expect(host().querySelector('[data-testid="external-login-status-error"]')).toBeNull();
    expect(stateOf('github')).toBe(text('off', 'Off'));
  });

  it('shows each provider as off with its callback URL when none is enabled', async () => {
    await render({
      providers: [provider(), provider({ provider: 'google', callbackUrl: 'https://cb/google' })],
    });

    expect(stateOf('github')).toBe(text('off', 'Off'));
    expect(stateOf('google')).toBe(text('off', 'Off'));
    expect(hintOf('github')).toBeNull();
    // 回调地址在开启之前就要拿去提供方登记，且落在自己的卡片上
    expect(callbackOf('google')).toBe('https://cb/google');
    expect(callbackOf('github')).toBe('https://id.example.test/api/v1/external-auth/github/signin');
  });

  it.each([
    [{ hasClientId: true }, 'missingClientSecret', 'the client secret is missing'],
    [{ hasClientSecret: true }, 'missingClientId', 'the client ID is missing'],
    [{}, 'missingBoth', 'the client ID and client secret are missing'],
  ])('says what is missing when enabled but not active (%o)', async (configured, key, english) => {
    await render({ providers: [provider({ enabled: true, ...configured })] });

    expect(stateOf('github')).toBe(text('inactive', 'Not active'));
    expect(hintOf('github')).toContain(text(key, english));
    expect(hintOf('google')).toBeNull();
  });

  // 凭据齐全仍未生效：本实例还没刷新到，不能说成缺东西
  it('asks to refresh later when enabled and complete but not active yet', async () => {
    await render({
      providers: [provider({ enabled: true, hasClientId: true, hasClientSecret: true })],
    });

    expect(hintOf('github')).toBe(
      text(
        'pending',
        'Enabled and configured, but this instance has not picked it up yet. Refresh in about 30 seconds.',
      ),
    );
  });

  it('asks to refresh later when turned off but still active', async () => {
    await render({ providers: [provider({ active: true })] });

    expect(stateOf('github')).toBe(text('active', 'Active'));
    expect(hintOf('github')).toBe(
      text(
        'stopping',
        'Turned off, but this instance still offers it. Refresh in about 30 seconds.',
      ),
    );
  });

  it('shows an active provider without a hint', async () => {
    await render({
      providers: [
        provider({ enabled: true, hasClientId: true, hasClientSecret: true, active: true }),
      ],
    });

    expect(stateOf('github')).toBe(text('active', 'Active'));
    expect(hintOf('github')).toBeNull();
  });

  function credentialOf(name: string, field: 'client-id' | 'client-secret'): string | null {
    return (
      cardOf(name)
        .querySelector(`[data-testid="external-login-${field}"] dd`)
        ?.textContent?.trim() ?? null
    );
  }

  // 机密设置不回显：密钥来自部署配置时口令框显示"未设置"，未启用的提供方也要能看出凭据已配置
  it('shows credentials from deployment as set while the provider is off', async () => {
    await render({ providers: [provider({ hasClientSecret: true })] });

    expect(stateOf('github')).toBe(text('off', 'Off'));
    // 按真实可见判定：只查文字的话，隐藏起来的一行也能通过
    expect(
      cardOf('github')
        .querySelector<HTMLElement>('[data-testid="external-login-credentials"]')
        ?.checkVisibility(),
    ).toBe(true);
    expect(credentialOf('github', 'client-id')).toBe(text('notConfigured', 'Not set'));
    expect(credentialOf('github', 'client-secret')).toBe(text('configured', 'Set'));
  });

  it.each([
    [{ hasClientId: true, hasClientSecret: false }, 'configured', 'notConfigured'],
    [{ hasClientId: false, hasClientSecret: true }, 'notConfigured', 'configured'],
    [{ hasClientId: true, hasClientSecret: true }, 'configured', 'configured'],
  ])('reports each credential on its own (%o)', async (flags, idKey, secretKey) => {
    const english: Record<string, string> = { configured: 'Set', notConfigured: 'Not set' };
    await render({ providers: [provider({ enabled: true, ...flags })] });

    expect(credentialOf('github', 'client-id')).toBe(text(idKey, english[idKey]));
    expect(credentialOf('github', 'client-secret')).toBe(text(secretKey, english[secretKey]));
  });

  it('copies the callback URL of the clicked card', async () => {
    await render({
      providers: [provider(), provider({ provider: 'google', callbackUrl: 'https://cb/google' })],
    });

    cardOf('google')
      .querySelector<HTMLButtonElement>('[data-testid="external-login-copy-callback"]')!
      .click();
    await settle();

    expect(writeText).toHaveBeenCalledExactlyOnceWith('https://cb/google');
    // 「已复制」只落在点过的那一张卡片上
    expect(
      cardOf('google')
        .querySelector('[data-testid="external-login-copy-callback"]')
        ?.getAttribute('aria-label'),
    ).toBe(text('copied', 'Copied'));
    expect(
      cardOf('github')
        .querySelector('[data-testid="external-login-copy-callback"]')
        ?.getAttribute('aria-label'),
    ).toBe(text('copyCallbackUrl', 'Copy callback URL'));
  });

  // 本组设置保存或恢复默认后由设置区块递增 revision：状态跟着改动走，不必手动刷新
  it('fetches once for the group, then refetches on revision change and manual refresh', async () => {
    await render({ providers: [provider()] });
    expect(getStatus).toHaveBeenCalledOnce();

    getStatus.mockReturnValue(of({ providers: [provider({ enabled: true, active: true })] }));
    fixture.componentInstance.revision.set(1);
    await settle();

    expect(getStatus).toHaveBeenCalledTimes(2);
    expect(stateOf('github')).toBe(text('active', 'Active'));

    host()
      .querySelector<HTMLButtonElement>('[data-testid="external-login-status-refresh"]')!
      .click();
    await fixture.whenStable();
    expect(getStatus).toHaveBeenCalledTimes(3);
  });

  // 已有状态时刷新失败：保留原内容并提示，不把整块换成错误态
  it('keeps the last status when a refresh fails', async () => {
    await render({ providers: [provider({ enabled: true, active: true })] });

    getStatus.mockReturnValue(throwError(() => new Error('offline')));
    host()
      .querySelector<HTMLButtonElement>('[data-testid="external-login-status-refresh"]')!
      .click();
    await settle();

    expect(stateOf('github')).toBe(text('active', 'Active'));
    expect(host().querySelector('[data-testid="external-login-status-error"]')).not.toBeNull();
  });
});
