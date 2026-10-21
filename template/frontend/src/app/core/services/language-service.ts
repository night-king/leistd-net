import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import {
  computed,
  EnvironmentProviders,
  ErrorHandler,
  inject,
  Injectable,
  PLATFORM_ID,
  provideAppInitializer,
  signal,
} from '@angular/core';
import {
  LoadOptions,
  ProviderScope,
  provideTranslocoFallbackStrategy,
  Translation,
  TranslationLoadError,
  TranslocoFallbackStrategy,
  TranslocoService,
} from '@jsverse/transloco';
import { take } from 'rxjs';

export const SUPPORTED_LANGS = ['en', 'zh-CN'] as const;
export type Lang = (typeof SUPPORTED_LANGS)[number];

export const DEFAULT_LANG: Lang = 'en';

interface LangMeta {
  id: Lang;
  /** 该语言自身的本地名（下拉项文字），不依赖当前 culture。 */
  label: string;
  /** 触发按钮上的短码（如 EN / 中）。 */
  short: string;
}

/** 语言选择器展示元数据。 */
export const LANG_OPTIONS: LangMeta[] = [
  { id: 'en', label: 'English', short: 'EN' },
  { id: 'zh-CN', label: '中文', short: '中' },
];

/**
 * 语言服务：管理活动语言并驱动 Transloco 文案。
 *
 * localStorage 只存本设备偏好（未登录访客的显式选择，也是主体离开后的回落值）；账户语言只在内存
 * 生效，否则共享机器上 A 退出后 B 会在登录页看到 A 的语言。外壳在启动流成功后才渲染，启动流会等
 * 账户语言的词条激活（见 {@link applyAccountLang}），因此不会先闪一下默认语言。
 *
 * 切换先加载全局与已访问功能的词条、成功后才激活：一次性文案用同步 `translate()`，先激活会让途中
 * 返回的请求取到裸键。加载期间停在原语言，失败时保持原语言并上报；连续切换只让最后一次生效。
 *
 * Transloco 的活动语言只由本服务决定：它在加载失败时自动回落、失败后下一次成功自行激活，都由
 * {@link provideLanguageFallbackStrategy} 阻止，否则本服务、Transloco 与 `<html lang>` 会不一致。
 */
@Injectable({ providedIn: 'root' })
export class LanguageService {
  static readonly STORAGE_KEY = 'app_lang';

  private readonly platformId = inject(PLATFORM_ID);
  private readonly transloco = inject(TranslocoService);
  private readonly document = inject(DOCUMENT);
  private readonly errorHandler = inject(ErrorHandler);

  private readonly activeLangState = signal<Lang>(this.loadDeviceLang());

  /** 在途的切换：目标语言与它的完成信号。相同目标的重复请求复用它。 */
  private pending: { lang: Lang; done: Promise<void> } | undefined;
  /** 每次发起切换加一；加载完成时编号已变，说明被后发起的切换取代，不再激活。 */
  private generation = 0;

  /** 已成功进入的功能：切换语言时连同这些词条一起预加载，覆盖仍打开的弹窗。 */
  private readonly loadedScopes = new Map<string, ProviderScope>();
  /** Transloco 缓存加载管道会保留同一个 options 对象，失败后重试需复用并清理它的计数。 */
  private readonly loadOptions = new Map<string, LoadOptions>();
  /** 内联资源只复用在途与成功的 Promise；拒绝后移除，显式重试会重新执行加载函数。 */
  private readonly inlineLoads = new Map<string, Promise<Translation>>();

  readonly activeLang = this.activeLangState.asReadonly();
  readonly options = LANG_OPTIONS;
  readonly currentMeta = computed(
    () => LANG_OPTIONS.find((o) => o.id === this.activeLang()) ?? LANG_OPTIONS[0],
  );

  /** 初始语言的词条落定；取不到时已退回默认语言。首帧前由 {@link provideLanguageInitializer} 等它。 */
  readonly initialized: Promise<void>;

  constructor() {
    // 初始语言没有「原语言」可停留，先激活；首帧由初始化器挡到词条落定，取不到时退回默认语言（见 switchTo）。
    const initial = this.activeLang();
    this.applyLang(initial);
    this.initialized = this.switchTo(initial);
  }

  /** 切到指定语言并记为本设备偏好；只用于未登录时的显式选择，已登录走 {@link applyAccountLang}。 */
  setDeviceLang(lang: Lang): Promise<void> {
    if (isPlatformBrowser(this.platformId)) {
      localStorage.setItem(LanguageService.STORAGE_KEY, lang);
    }
    return this.setActive(lang);
  }

  /** 应用账户设置里的语言，只改内存不落盘（理由见类注释）。 */
  applyAccountLang(lang: Lang): Promise<void> {
    return this.setActive(lang);
  }

  /** 回到本设备偏好：主体离开、或新主体的设置没加载上来时用。 */
  resetToDeviceLang(): Promise<void> {
    return this.setActive(this.loadDeviceLang());
  }

  /**
   * 加载目标语言的词条，成功后激活；返回的 Promise 在词条落定时完成，从不 reject。相同目标在途时
   * 复用；已激活的语言也走一遍加载（可能是加载失败后停留的初始语言），同时取代在途的切换。
   */
  private setActive(lang: Lang): Promise<void> {
    if (this.pending?.lang === lang) {
      return this.pending.done;
    }
    return this.switchTo(lang);
  }

  /**
   * 加载 `lang`，成功则激活；被后发起的切换取代时不做任何事。失败时停在原语言；原语言就是这次
   * 没加载上的语言时没有词条可停留，退回默认语言（同样受取代约束）。
   */
  private switchTo(lang: Lang): Promise<void> {
    const generation = ++this.generation;
    const done = (async () => {
      let loaded = await this.load(lang);
      const ready = new Set<string>();
      // 等待过程中新增的 scope 也要加载；最终检查与激活在同一个微任务内，避免导航插入空隙。
      while (loaded && generation === this.generation) {
        const scopes = [...this.loadedScopes.values()].filter((scope) => !ready.has(scope.scope));
        const results = await Promise.all(scopes.map((scope) => this.loadScope(scope, lang)));
        loaded = results.every((success) => success);
        scopes.forEach((scope) => ready.add(scope.scope));
        if ([...this.loadedScopes.keys()].every((scope) => ready.has(scope))) {
          break;
        }
      }
      if (generation !== this.generation) {
        return;
      }
      this.pending = undefined;
      if (loaded) {
        this.activeLangState.set(lang);
        this.applyLang(lang);
        return;
      }
      if (lang === this.activeLang() && lang !== DEFAULT_LANG) {
        await this.setActive(DEFAULT_LANG);
      }
    })();
    this.pending = { lang, done };
    return done;
  }

  /** 路由在创建功能组件前等待词条；等待期间切换语言时重新加载最终活动语言。 */
  async loadScopes(provided: readonly (string | ProviderScope)[]): Promise<boolean> {
    await this.initialized;
    const scopes = provided.map((scope) => (typeof scope === 'string' ? { scope } : scope));
    while (true) {
      const lang = this.activeLang();
      const results = await Promise.all(scopes.map((scope) => this.loadScope(scope, lang)));
      if (results.some((loaded) => !loaded)) {
        return false;
      }
      scopes.forEach((scope) => this.loadedScopes.set(scope.scope, scope));
      if (lang === this.activeLang()) {
        return true;
      }
    }
  }

  /** 在合并词条前应用官方 alias 映射；内联资源成功后才交给官方 load API 缓存。 */
  private async loadScope(scope: ProviderScope, lang: Lang): Promise<boolean> {
    if (scope.alias) {
      this.transloco.config.scopeMapping ??= {};
      this.transloco.config.scopeMapping[scope.scope] = scope.alias;
    }
    const inlineLoader = scope.loader
      ? Object.fromEntries(
          Object.entries(scope.loader).map(([language, loader]) => [
            `${scope.scope}/${language}`,
            () => this.loadInline(`${scope.scope}/${language}`, loader),
          ]),
        )
      : undefined;
    const path = `${scope.scope}/${lang}`;
    if (inlineLoader) {
      // 8.4 创建管道时就调用 inline loader，重订阅会复用拒绝的 Promise。
      // 先等待目标资源和缺词回落资源，避免把失败的 Promise 放入库缓存；不访问私有 cache。
      const { fallbackLang, missingHandler } = this.transloco.config;
      const fallback = Array.isArray(fallbackLang) ? fallbackLang[0] : fallbackLang;
      const languages =
        missingHandler.useFallbackTranslation && fallback && fallback !== lang
          ? [lang, fallback]
          : [lang];
      try {
        await Promise.all(
          languages.map((language) => {
            const resource = `${scope.scope}/${language}`;
            const loader = inlineLoader[resource];
            if (!loader) {
              throw new Error(`Missing inline loader for ${resource}`);
            }
            return loader();
          }),
        );
      } catch (cause) {
        const error = new TranslationLoadError(path, [], true);
        error.cause = cause;
        this.errorHandler.handleError(error);
        return false;
      }
    }
    return this.load(path, inlineLoader);
  }

  private loadInline(path: string, loader: () => Promise<Translation>): Promise<Translation> {
    const cached = this.inlineLoads.get(path);
    if (cached) {
      return cached;
    }
    // 微任务内执行也接住同步抛错；并发请求共用本次加载，失败后下一次调用重新执行。
    const loading = Promise.resolve()
      .then(loader)
      .catch((error: unknown) => {
        this.inlineLoads.delete(path);
        throw error;
      });
    this.inlineLoads.set(path, loading);
    return loading;
  }

  /**
   * 加载 `lang` 的词条，返回是否成功；失败交给全局错误处理，从不 reject。
   *
   * HTTP/冷 Observable 的失败管道再次订阅会重新请求；inline Promise 不具备这个性质，
   * 由 loadScope 在交给此处前预加载，失败时不创建库管道，成功后复用资源与官方缓存。
   */
  private load(lang: string, inlineLoader?: LoadOptions['inlineLoader']): Promise<boolean> {
    const options = this.loadOptions.get(lang) ?? {};
    if (inlineLoader) {
      options.inlineLoader = inlineLoader;
    }
    this.loadOptions.set(lang, options);
    return new Promise((resolve) => {
      let loaded = false;
      this.transloco
        .load(lang, options)
        .pipe(take(1))
        .subscribe({
          next: () => (loaded = true),
          error: (error: unknown) => {
            // 8.4 在调用回落策略前写入计数；策略抛错后留下半初始化状态，第二次失败会读到空回落列表。
            // 清理原加载管道持有的对象，确保每次失败都经过策略并保留 TranslationLoadError。
            delete options.failedCounter;
            this.errorHandler.handleError(error);
            resolve(false);
          },
          // 不发射就结束只在应用销毁时出现，无需上报
          complete: () => resolve(loaded),
        });
    });
  }

  private applyLang(lang: Lang): void {
    // 同步订阅 Transloco 语言事件的读者也必须看到一致的 <html lang>。
    this.document.documentElement.lang = lang;
    // 已是该语言就不再设置：Transloco 的语言流对同值也会再发一次，结构指令会白白重建一轮上下文。
    if (this.transloco.getActiveLang() !== lang) {
      this.transloco.setActiveLang(lang);
    }
  }

  /**
   * 本设备的语言：显式选过的 → 浏览器语言 → 默认语言。跳过浏览器语言会让中文浏览器首次进入
   * 也看到英文。
   */
  private loadDeviceLang(): Lang {
    if (!isPlatformBrowser(this.platformId)) {
      return DEFAULT_LANG;
    }

    let stored: string | null = null;
    try {
      stored = localStorage.getItem(LanguageService.STORAGE_KEY);
    } catch {
      // 隐私模式/配额：读不到就当没选过
    }

    if (SUPPORTED_LANGS.includes(stored as Lang)) {
      return stored as Lang;
    }

    return systemLang() ?? DEFAULT_LANG;
  }
}

/**
 * 首帧前等初始语言的词条落定：根组件的启动页不在结构指令里，渲染后立即发出的一次性提示用同步
 * `translate()`。取不到时已退回默认语言，这里不 reject，否则应用连启动失败页都没有。
 */
export function provideLanguageInitializer(): EnvironmentProviders {
  return provideAppInitializer(() => inject(LanguageService).initialized);
}

class NoAutomaticFallback implements TranslocoFallbackStrategy {
  getNextLangs(lang: string): string[] {
    // 8.4 在策略返回后记录 failedLangs，之后任意 scope 成功都会自行激活语言。
    // 在官方扩展点直接终止加载，既不自动回落，也不进入这段恢复状态。
    throw new TranslationLoadError(lang, [], lang.includes('/'));
  }
}

/**
 * 词条加载失败时 Transloco 不自行回落（默认策略会加载并激活回落语言，不管这次加载是否已被取代），
 * 改由 {@link LanguageService} 处理。缺词条时的回落文案读配置的 `fallbackLang`，不经过这里。
 * 应用与单测装配都要登记，否则测不到应用里的失败路径。
 */
export function provideLanguageFallbackStrategy(): EnvironmentProviders {
  return provideTranslocoFallbackStrategy(NoAutomaticFallback);
}

/**
 * 浏览器偏好的语言里第一个本端支持的：按 `navigator.languages` 顺序先精确匹配，再按主语言子标签
 * 匹配（`en-GB` → `en`、`zh-Hans-CN` → `zh-CN`）。Safari 与 Chrome 隐身模式会把它截成一项，
 * 取不到时退回 `navigator.language`。
 */
function systemLang(): Lang | undefined {
  let preferred: readonly string[];
  try {
    preferred = navigator.languages?.length ? navigator.languages : [navigator.language];
  } catch {
    return undefined;
  }

  for (const tag of preferred) {
    if (!tag) {
      continue;
    }

    const exact = SUPPORTED_LANGS.find((lang) => lang.toLowerCase() === tag.toLowerCase());
    if (exact) {
      return exact;
    }

    const primary = primarySubtag(tag);
    const byPrimary = SUPPORTED_LANGS.find((lang) => primarySubtag(lang) === primary);
    if (byPrimary) {
      return byPrimary;
    }
  }

  return undefined;
}

/** 语言标签的主语言子标签（`zh-Hans-CN` → `zh`）；解析不了就取第一段。 */
function primarySubtag(tag: string): string {
  try {
    return new Intl.Locale(tag).language.toLowerCase();
  } catch {
    return tag.split('-')[0]!.toLowerCase();
  }
}
