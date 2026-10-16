import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  TemplateRef,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheck, lucideCopy, lucideRefreshCw } from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { finalize, Subscription } from 'rxjs';

import { applicationErrorMessage } from '../../../../../../core/errors/application-http-error';
import { SettingService } from '../../../../../../core/settings/setting-service';
// prettier-ignore
import {
  ExternalLoginProviderStatusOutputDto,
  ExternalLoginStatusOutputDto,
  SettingOutputDto,
} from '../../../../../../core/settings/setting.dto';
import { StatusDot } from '../../../../../../shared/directives/status-dot';
import { injectCopyToClipboard } from '../../../../../../shared/utils/clipboard';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif

/** 外部登录设置的分组标识，与后端 `SettingConstant.Groups.ExternalLogin` 一致。 */
export const EXTERNAL_LOGIN_GROUP = 'ExternalLogin';

/**
 * 设置名约定 `ExternalLogin.{提供方}.{字段}`（与后端 `SettingConstant.ExternalLogin` 一致）。
 * 设置名是跨前后端的字符串契约，按它分区，不另列提供方清单。
 */
const PROVIDER_SETTING_NAME = /^ExternalLogin\.([^.]+)\.([^.]+)$/;

/** 分区内的字段顺序：先开关、再凭据；约定外的字段排在后面，保持下发顺序。 */
const FIELD_ORDER = ['Enabled', 'ClientId', 'ClientSecret'];

/** 提供方的展示名，按状态端点的小写标识查；认不出的提供方退回设置名里的原文。 */
const PROVIDER_LABELS: Record<string, string> = { github: 'GitHub', google: 'Google' };

/** 一个提供方的设置：`provider` 是设置名中段的原文（如 `Github`）。 */
export interface ExternalLoginProviderSettings {
  provider: string;
  settings: SettingOutputDto[];
}

/** 外部登录分组按提供方拆开的结果；`others` 是不符合命名约定的设置，照常按普通行渲染，不能静默消失。 */
export interface ExternalLoginSettingsPartition {
  providers: ExternalLoginProviderSettings[];
  others: SettingOutputDto[];
}

/** 按设置名约定把外部登录分组拆成每个提供方一组；提供方按首次出现的顺序（即后端下发顺序）排列。 */
export function partitionExternalLoginSettings(
  settings: readonly SettingOutputDto[],
): ExternalLoginSettingsPartition {
  const providers = new Map<string, SettingOutputDto[]>();
  const others: SettingOutputDto[] = [];
  for (const setting of settings) {
    const provider = PROVIDER_SETTING_NAME.exec(setting.name)?.[1];
    if (provider === undefined) {
      others.push(setting);
      continue;
    }

    providers.set(provider, [...(providers.get(provider) ?? []), setting]);
  }

  return {
    providers: [...providers].map(([provider, items]) => ({
      provider,
      settings: [...items].sort((a, b) => fieldRank(a) - fieldRank(b)),
    })),
    others,
  };
}

function fieldRank(setting: SettingOutputDto): number {
  const rank = FIELD_ORDER.indexOf(PROVIDER_SETTING_NAME.exec(setting.name)?.[2] ?? '');
  return rank === -1 ? FIELD_ORDER.length : rank;
}

/** 分区的生效状态；`hint` 为说明词条键，空串表示状态本身已说清楚。 */
interface ProviderState {
  state: 'active' | 'inactive' | 'off';
  /** 凭据是否已配置（设置或部署配置）：机密设置不回显，部署提供的密钥只能从这里看出来。 */
  hasClientId: boolean;
  hasClientSecret: boolean;
  hint: string;
  callbackUrl: string;
}

/** 一个提供方分区：设置行由设置区块的行模板渲染，状态在取到之前为 `null`。 */
interface ProviderCard {
  /** 状态端点的提供方标识（小写），也作分区的稳定键。 */
  key: string;
  label: string;
  settings: SettingOutputDto[];
  status: ProviderState | null;
}

/** 设置区块的行模板上下文：分区只决定放在哪，行的控件、校验与写入都留在设置区块。 */
export interface SettingRowContext {
  $implicit: SettingOutputDto;
}

/**
 * 「外部登录」分组：每个提供方一节，节里是该提供方的设置行，标题旁是本实例上的生效状态，
 * 节内说明缺什么，底部是要登记到提供方的回调地址。
 *
 * 状态整组取一次，本组设置保存或恢复默认后重取。机密不回显，密钥来自部署配置时设置行看不出已配置，
 * 所以由状态端点给出。状态反映响应本次请求的实例，其他实例经刷新周期（约 30 秒）各自跟上。
 */
@Component({
  selector: 'app-external-login-providers',
  // prettier-ignore
  imports: [
    NgIcon,
    NgTemplateOutlet,
    HlmBadge,
    HlmButton,
    HlmSpinner,
    StatusDot,
    ...HlmFieldImports,
    ...HlmTooltipImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideCheck, lucideCopy, lucideRefreshCw })],
  templateUrl: './external-login-providers.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExternalLoginProviders {
  private readonly settingService = inject(SettingService);
  private readonly destroyRef = inject(DestroyRef);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  protected readonly clipboard = injectCopyToClipboard();

  /** 按提供方拆好的设置，见 {@link partitionExternalLoginSettings}。 */
  readonly providers = input.required<readonly ExternalLoginProviderSettings[]>();

  /** 设置区块的行模板：与其他分组共用同一套行渲染，不另写一份表单。 */
  readonly rowTemplate = input.required<TemplateRef<SettingRowContext>>();

  /** 本组设置的写入次数，由设置区块在每次保存或恢复默认成功后递增；变化即重新取状态。 */
  readonly revision = input(0);

  protected readonly status = signal<ExternalLoginStatusOutputDto | null>(null);
  protected readonly loading = signal(false);
  /** 最近一次读取的失败原因；已有状态时保留原内容，只在上方提示。 */
  protected readonly loadError = signal<string | null>(null);
  /** 最近复制了哪个提供方的回调地址：同一个复制动作服务所有分区，「已复制」只落在点过的那一节。 */
  protected readonly copiedProvider = signal<string | null>(null);

  protected readonly cards = computed<ProviderCard[]>(() => {
    const statuses = this.status()?.providers ?? [];
    return this.providers().map(({ provider, settings }) => {
      const key = provider.toLowerCase();
      const status = statuses.find((item) => item.provider === key);
      return {
        key,
        label: PROVIDER_LABELS[key] ?? provider,
        settings,
        status: status ? stateOf(status) : null,
      };
    });
  });

  /** 尚未返回的上一次读取。新一次开始时取消它：晚到的旧响应不能覆盖保存后的新状态。 */
  private pending?: Subscription;

  constructor() {
    // 首次读取与"本组保存后重取"走同一处：输入初值也会触发一次
    effect(() => {
      this.revision();
      untracked(() => this.load());
    });
  }

  protected load(): void {
    this.pending?.unsubscribe();
    this.loading.set(true);
    this.pending = this.settingService
      .getExternalLoginStatus()
      .pipe(
        finalize(() => this.loading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (status) => {
          this.loadError.set(null);
          this.status.set(status);
        },
        error: (error: unknown) => this.loadError.set(applicationErrorMessage(error)),
      });
  }

  protected copy(card: ProviderCard, callbackUrl: string): void {
    void this.clipboard.copy(callbackUrl).then((copied) => {
      if (copied) {
        this.copiedProvider.set(card.key);
      }
    });
  }

  protected isCopied(card: ProviderCard): boolean {
    return this.clipboard.copied() && this.copiedProvider() === card.key;
  }
}

function stateOf(status: ExternalLoginProviderStatusOutputDto): ProviderState {
  return {
    state: status.active ? 'active' : status.enabled ? 'inactive' : 'off',
    hasClientId: status.hasClientId,
    hasClientSecret: status.hasClientSecret,
    hint: hintOf(status),
    callbackUrl: status.callbackUrl,
  };
}

/** 状态说明：开着却没生效时说出缺什么；凭据齐全仍未生效、或已关闭仍在提供，是本实例还没刷新到。 */
function hintOf({
  enabled,
  active,
  hasClientId,
  hasClientSecret,
}: ExternalLoginProviderStatusOutputDto): string {
  if (enabled && !active) {
    if (!hasClientId && !hasClientSecret) {
      return 'settings.externalLoginProviders.missingBoth';
    }
    if (!hasClientId) {
      return 'settings.externalLoginProviders.missingClientId';
    }
    if (!hasClientSecret) {
      return 'settings.externalLoginProviders.missingClientSecret';
    }
    return 'settings.externalLoginProviders.pending';
  }

  if (!enabled && active) {
    return 'settings.externalLoginProviders.stopping';
  }

  return '';
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'settings.externalLoginProviders.description':
    'Status shows the instance that answered this request.',
  'settings.externalLoginProviders.active': 'Active',
  'settings.externalLoginProviders.inactive': 'Not active',
  'settings.externalLoginProviders.off': 'Off',
  'settings.externalLoginProviders.clientId': 'Client ID',
  'settings.externalLoginProviders.clientSecret': 'Client secret',
  'settings.externalLoginProviders.configured': 'Set',
  'settings.externalLoginProviders.notConfigured': 'Not set',
  'settings.externalLoginProviders.missingClientId':
    'Enabled but not active: the client ID is missing.',
  'settings.externalLoginProviders.missingClientSecret':
    'Enabled but not active: the client secret is missing.',
  'settings.externalLoginProviders.missingBoth':
    'Enabled but not active: the client ID and client secret are missing.',
  'settings.externalLoginProviders.pending':
    'Enabled and configured, but this instance has not picked it up yet. Refresh in about 30 seconds.',
  'settings.externalLoginProviders.stopping':
    'Turned off, but this instance still offers it. Refresh in about 30 seconds.',
  'settings.externalLoginProviders.callbackUrl': 'Callback URL',
  'settings.externalLoginProviders.copyCallbackUrl': 'Copy callback URL',
  'settings.externalLoginProviders.copied': 'Copied',
  'settings.externalLoginProviders.loadFailed': "Couldn't load the sign-in status",
  'common.refresh': 'Refresh',
  'common.retry': 'Retry',
};
//#endif
