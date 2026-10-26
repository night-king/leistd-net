import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

//#if (IncludeLocalization)
import { Lang, LanguageService, SUPPORTED_LANGS } from './language-service';
//#endif
import { THEME_MODES, ThemeMode, ThemeService } from './theme-service';
import {
  AccountPreferenceField,
  AccountPreferencesDto,
  AccountPreferencesOutputDto,
  AccountThemeMode,
} from '../../shared/dtos/account-preferences.dto';
import { ApplicationHttpError } from '../errors/application-http-error';

/** 解析后的一份偏好：每个字段都在，未设置为 null（线上缺省与 null 同义）。 */
export interface AccountPreferences {
  language: string | null;
  timeZone: string | null;
  theme: AccountThemeMode | null;
}

/** 生效值与个人覆盖成对的一份已知偏好。 */
interface KnownPreferences {
  preferences: AccountPreferences;
  overrides: AccountPreferences;
}

/**
 * 一次偏好写入的结果：
 * - `saved`：已保存，界面已按响应里的完整偏好更新；
 * - `rejected`：服务端明确拒绝，偏好不变；
 * - `unconfirmed`：没有可判定的响应（断网、超时、写请求的 5xx），可能已经保存，不重放，下次重查 `/me` 时对齐；
 * - `discarded`：主体已经离开，这一项不再发出或不再应用。
 */
export type PreferenceWriteOutcome =
  | { status: 'saved' }
  | { status: 'rejected'; error: unknown }
  | { status: 'unconfirmed'; error: unknown }
  | { status: 'discarded' };

/** 读取 `/me` 时的应用方式：新主体读不到偏好要清掉前一人的，同一主体后台重查读不到则保留已应用的。 */
export type PreferenceReadKind = 'establish' | 'refresh';

/**
 * 会话的账户偏好状态：语言、时区、外观的唯一前端来源。偏好由身份服务持有并计算，经 `GET /auth/me` 读取、
 * `PATCH /auth/me/preferences` 写入（三种部署形态同一契约），不经设置快照。
 *
 * 读取用 {@link beginRead} 取号、{@link applyRead} 应用：期间有写入成功或主体离开，号就过期，迟到的旧 `/me`
 * 不再覆盖。写入一律经 {@link write} 排成一个队列：偏好面板与页头切换器各自发起的写入若并发，后到的旧完整响应
 * 会盖掉先到的新值，串行后每一项都在前一项的结果之上。
 */
/** 一次 `/me` 读到的偏好及发起时取的号（见 {@link AccountPreferencesService.beginRead}）。 */
export interface PreferenceRead {
  readonly ticket: number;
  readonly read: AccountPreferencesOutputDto;
}

@Injectable({ providedIn: 'root' })
export class AccountPreferencesService {
  static readonly url = '/api/v1/auth/me/preferences';

  private readonly http = inject(HttpClient);
  private readonly themeService = inject(ThemeService);
  //#if (IncludeLocalization)
  private readonly languageService = inject(LanguageService);
  //#endif

  /** 当前已知的偏好；`null` 表示未知（未登录、尚未读到、或新主体读不到）。 */
  private readonly state = signal<KnownPreferences | null>(null);
  /** 写入成功与主体离开时加一：读取发起时记下，回来时已变就不应用。 */
  private revision = 0;
  /** 主体代次：主体离开加一，队列里属于前一个主体、尚未发出的写入随之作废。 */
  private session = 0;
  /** 写入队列的队尾。 */
  private queue: Promise<unknown> = Promise.resolve();

  /** 生效偏好；未知时为 `null`。 */
  readonly preferences = computed(() => this.state()?.preferences ?? null);
  /** 个人覆盖原值；未知时为 `null`，字段为 `null` 表示没有覆盖。 */
  readonly overrides = computed(() => this.state()?.overrides ?? null);
  /** 偏好是否已知。偏好面板据此区分"暂时无法读取"与"没有覆盖"。 */
  readonly known = computed(() => this.state() !== null);

  /** 展示时区（IANA 名）；未设置或未知时为 `undefined`，按浏览器本地时区展示。时间统一在此读取后换算。 */
  readonly timeZone = computed(() => this.state()?.preferences.timeZone ?? undefined);

  /**
   * 日期与数字书写用的 locale，由实际活动语言驱动，不直接读偏好里的语言：偏好未设置时界面按设备语言显示，
   * 读偏好会让日期写法与文案分叉。取不到时为 `undefined`，由 `appDate` 回落到 `YYYY-MM-DD`。
   */
  readonly displayLocale = computed(() => this.resolveLocale());

  /** 发起一次 `/me` 读取前取号，回来时交给 {@link applyRead}。 */
  beginRead(): number {
    return this.revision;
  }

  /**
   * 应用一次 `/me` 读到的偏好。号已过期（期间有写入成功或主体离开）时什么都不做。
   * 读不到（两个对象缺一或为 null）时：新主体清掉前一人的语言与时区、退回设备；同一主体保留已应用的值。
   * 返回的 Promise 在语言词条落定时完成，启动流据此让首帧就是账户语言。
   */
  applyRead(
    ticket: number,
    read: Partial<AccountPreferencesOutputDto>,
    kind: PreferenceReadKind,
  ): Promise<void> {
    if (ticket !== this.revision) {
      return Promise.resolve();
    }

    const known = toKnown(read);
    if (!known) {
      if (kind === 'refresh') {
        return Promise.resolve();
      }

      this.state.set(null);
      return this.resetLanguage();
    }

    return this.apply(known, false);
  }

  /**
   * 写入一项偏好（`null` 清除个人覆盖），排在队列里：前一项的结果应用完才发下一项，单项失败不卡住后面的。
   * 成功时用响应里的完整偏好更新界面；结果的分类见 {@link PreferenceWriteOutcome}。
   */
  write<K extends AccountPreferenceField>(
    field: K,
    value: AccountPreferences[K],
  ): Promise<PreferenceWriteOutcome> {
    const session = this.session;
    const run = async (): Promise<PreferenceWriteOutcome> => {
      // 排队期间主体已经离开：不把前一个人的写入发给后一个人
      if (session !== this.session) {
        return { status: 'discarded' };
      }

      try {
        const result = await firstValueFrom(
          this.http.patch<AccountPreferencesOutputDto>(
            AccountPreferencesService.url,
            { [field]: value },
            { headers: new HttpHeaders({ 'Content-Type': 'application/merge-patch+json' }) },
          ),
        );
        const known = toKnown(result);
        if (session !== this.session) {
          return { status: 'discarded' };
        }

        // 号先过期，再应用：在途的旧 /me 回来时不会盖掉这次写入的结果
        this.revision++;
        if (known) {
          await this.apply(known, true);
        }
        return { status: 'saved' };
      } catch (error: unknown) {
        return isUnconfirmed(error)
          ? { status: 'unconfirmed', error }
          : { status: 'rejected', error };
      }
    };

    const next = this.queue.then(run, run);
    this.queue = next.catch(() => undefined);
    return next;
  }

  /** 主体离开：偏好作废，排队未发的写入丢弃，语言退回设备。外观保留最近一次值（见 {@link ThemeService}）。 */
  clear(): Promise<void> {
    this.session++;
    this.revision++;
    this.state.set(null);
    return this.resetLanguage();
  }

  /** @param animate 本人刚改的外观带过渡切换；读取 `/me` 得到的值直接生效。 */
  private apply(known: KnownPreferences, animate: boolean): Promise<void> {
    this.state.set(known);
    // 外观未设置即跟随系统
    const theme = known.preferences.theme ?? 'system';
    if (animate && theme !== this.themeService.mode()) {
      this.themeService.transitionTo(theme);
    } else {
      this.themeService.applyAccountMode(theme);
    }
    //#if (IncludeLocalization)
    const language = known.preferences.language;
    if (language && (SUPPORTED_LANGS as readonly string[]).includes(language)) {
      return this.languageService.applyAccountLang(language as Lang);
    }

    // 未设置或本版不支持的语言退回设备；什么都不做会沿用上一个主体的语言
    return this.languageService.resetToDeviceLang();
    //#else
    return Promise.resolve();
    //#endif
  }

  private resetLanguage(): Promise<void> {
    //#if (IncludeLocalization)
    return this.languageService.resetToDeviceLang();
    //#else
    return Promise.resolve();
    //#endif
  }

  //#if (IncludeLocalization)
  /** 当前界面语言：读运行时的活动语言，未登录时的设备选择与切换后的语言都在其中。 */
  //#else
  /** 当前界面语言：单语言项目取浏览器语言，取不到时为 `undefined`。 */
  //#endif
  private resolveLocale(): string | undefined {
    //#if (IncludeLocalization)
    return this.languageService.activeLang();
    //#else
    try {
      return navigator.language || undefined;
    } catch {
      return undefined;
    }
    //#endif
  }
}

/** 两个对象齐全才算读到；缺一或为 null 即整份未知。字段缺省按未设置，外观取值不认识时也按未设置。 */
function toKnown(
  read: Partial<AccountPreferencesOutputDto> | null | undefined,
): KnownPreferences | null {
  const preferences = read?.preferences;
  const overrides = read?.preferenceOverrides;
  if (!preferences || !overrides) {
    return null;
  }

  return { preferences: normalize(preferences), overrides: normalize(overrides) };
}

function normalize(value: AccountPreferencesDto): AccountPreferences {
  return {
    language: value.language ?? null,
    timeZone: value.timeZone ?? null,
    theme: THEME_MODES.includes(value.theme as ThemeMode) ? (value.theme ?? null) : null,
  };
}

/**
 * 写入结果无法判定：没有收到响应、写请求遇到 5xx 或 2xx 解析失败。拦截器已把这些归为 `uncertain`；
 * 未经拦截器的原始错误（单测）按同一判据：状态 0 或 5xx。其余（4xx 的明确拒绝）都是确定未保存。
 */
function isUnconfirmed(error: unknown): boolean {
  if (error instanceof ApplicationHttpError) {
    return error.category === 'uncertain';
  }

  if (error instanceof HttpErrorResponse) {
    return error.status === 0 || error.status >= 500;
  }

  return true;
}
