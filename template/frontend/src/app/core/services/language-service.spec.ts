import {
  ApplicationInitStatus,
  EnvironmentProviders,
  ErrorHandler,
  Provider,
  provideZonelessChangeDetection,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  Translation,
  TranslationLoadError,
  TranslocoLoader,
  TranslocoService,
  translateSignal,
} from '@jsverse/transloco';
import { defer, Subject, throwError } from 'rxjs';

import { LanguageService, provideLanguageInitializer } from './language-service';
import { provideTranslocoTesting } from '../i18n/transloco.testing';

import type { Mock } from 'vitest';

/**
 * 可控加载器：每种语言的词条要等用例显式放行才到达，模拟 JSON 资源慢于语言切换。
 *
 * 与 HTTP 加载器一样是冷的：每次订阅才发一次请求。Transloco 的失败重试与再次加载都靠重新订阅，
 * 热的 Subject 会把上次的失败原样重放，测不出"再切过去会重新请求"。
 */
class ControllableLoader implements TranslocoLoader {
  private readonly gates = new Map<string, Subject<Translation>>();
  private readonly unavailable = new Set<string>();
  /** 每次请求记一条语言，按发出顺序。 */
  readonly requests: string[] = [];

  getTranslation(lang: string) {
    return defer(() => {
      this.requests.push(lang);
      if (this.unavailable.has(lang)) {
        return throwError(() => new Error(`${lang}.json unavailable`));
      }
      const gate = new Subject<Translation>();
      this.gates.set(lang, gate);
      return gate;
    });
  }

  resolve(lang: string, translation: Translation): void {
    const gate = this.gates.get(lang)!;
    this.gates.delete(lang);
    gate.next(translation);
    gate.complete();
  }

  /** 在途请求失败，此后的请求（含 Transloco 的自动重试）也失败，直到 {@link restore}。 */
  fail(lang: string): void {
    this.unavailable.add(lang);
    const gate = this.gates.get(lang)!;
    this.gates.delete(lang);
    gate.error(new Error(`${lang}.json unavailable`));
  }

  restore(lang: string): void {
    this.unavailable.delete(lang);
  }
}

/**
 * 语言切换的时序：先加载词条，成功后才激活。
 *
 * 请求回调里的一次性文案用同步 `translate()`，它要求活动语言的词条已加载；这组用例钉住切换途中
 * 取到的仍是原语言的真实文案、同一目标的重复请求等到词条落定才完成，以及加载失败时停在原语言。
 */
describe('LanguageService', () => {
  let loader: ControllableLoader;
  let errorHandler: { handleError: Mock };

  function configure(extraProviders: (Provider | EnvironmentProviders)[] = []): void {
    loader = new ControllableLoader();
    errorHandler = { handleError: vi.fn().mockName('ErrorHandler.handleError') };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        ...provideTranslocoTesting(['en', 'zh-CN'], loader),
        { provide: ErrorHandler, useValue: errorHandler },
        ...extraProviders,
      ],
    });
  }

  /** 以英文起步且英文词条已到位，与首帧之后的应用状态一致。 */
  async function startInEnglish(): Promise<{
    service: LanguageService;
    transloco: TranslocoService;
  }> {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    configure();
    const transloco = TestBed.inject(TranslocoService);
    const service = TestBed.inject(LanguageService);
    loader.resolve('en', { greeting: 'Hello' });
    await service.initialized;
    return { service, transloco };
  }

  /** 以中文起步且中文词条已到位，英文从未加载：失败时没有已加载的回落语言可退。 */
  async function startInChinese(): Promise<{
    service: LanguageService;
    transloco: TranslocoService;
  }> {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'zh-CN');
    configure();
    const transloco = TestBed.inject(TranslocoService);
    const service = TestBed.inject(LanguageService);
    loader.resolve('zh-CN', { greeting: '你好' });
    await service.initialized;
    return { service, transloco };
  }

  /** 四处活动语言一致，且同步 `translate()` 取到的是该语言的真实文案。 */
  function expectActive(
    service: LanguageService,
    transloco: TranslocoService,
    lang: string,
    greeting: string,
  ): void {
    expect(service.activeLang()).toBe(lang);
    expect(transloco.getActiveLang()).toBe(lang);
    expect(document.documentElement.lang).toBe(lang);
    expect(transloco.translate('greeting')).toBe(greeting);
  }

  /** 记下此后 Transloco 活动语言的每一次变化（不含订阅时的当前值）。 */
  function recordLangChanges(transloco: TranslocoService): string[] {
    const changes: string[] = [];
    transloco.langChanges$.subscribe((lang) => changes.push(lang));
    changes.length = 0;
    return changes;
  }

  /** 让出足够的微任务，让已完成的加载走完 Promise 回调。 */
  async function flushMicrotasks(): Promise<void> {
    for (let i = 0; i < 10; i++) {
      await Promise.resolve();
    }
  }

  /** Promise 是否已完成：让出若干微任务后看回调是否跑过。 */
  async function isSettled(promise: Promise<unknown>): Promise<boolean> {
    let settled = false;
    void promise.then(() => (settled = true));
    for (let i = 0; i < 10; i++) {
      await Promise.resolve();
    }
    return settled;
  }

  it('keeps the current language until the new translations arrive, so one-off texts are never bare keys', async () => {
    const { service, transloco } = await startInEnglish();
    const greeting = TestBed.runInInjectionContext(() => translateSignal('greeting'));

    const switched = service.applyAccountLang('zh-CN');

    // 请求结果先于中文词条返回：此刻取的一次性文案仍是英文真实文案，而不是裸键。
    expect(await isSettled(switched)).toBe(false);
    expect(transloco.activeLang()).toBe('en');
    expect(service.activeLang()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
    expect(transloco.translate('greeting')).toBe('Hello');
    expect(greeting()).toBe('Hello');

    loader.resolve('zh-CN', { greeting: '你好' });
    await switched;

    expect(transloco.activeLang()).toBe('zh-CN');
    expect(service.activeLang()).toBe('zh-CN');
    expect(document.documentElement.lang).toBe('zh-CN');
    expect(transloco.translate('greeting')).toBe('你好');
    expect(greeting()).toBe('你好');
  });

  it('preloads every visited scope before activating a language', async () => {
    const { service, transloco } = await startInEnglish();
    const entered = service.loadScopes(['users', 'roles']);
    await flushMicrotasks();
    loader.resolve('users/en', { title: 'Users' });
    loader.resolve('roles/en', { title: 'Roles' });
    await entered;

    const observed: string[][] = [];
    transloco.langChanges$.subscribe((lang) => {
      observed.push([
        lang,
        service.activeLang(),
        document.documentElement.lang,
        transloco.translate('users.title'),
      ]);
    });
    observed.length = 0;
    const switched = service.applyAccountLang('zh-CN');
    loader.resolve('zh-CN', { greeting: '你好' });
    await flushMicrotasks();
    loader.resolve('users/zh-CN', { title: '用户' });
    expect(await isSettled(switched)).toBe(false);
    expectActive(service, transloco, 'en', 'Hello');
    expect(transloco.translate('users.title')).toBe('Users');
    expect(transloco.translate('roles.title')).toBe('Roles');

    loader.resolve('roles/zh-CN', { title: '角色' });
    await switched;
    expectActive(service, transloco, 'zh-CN', '你好');
    expect(transloco.translate('users.title')).toBe('用户');
    expect(transloco.translate('roles.title')).toBe('角色');
    expect(observed).toEqual([['zh-CN', 'zh-CN', 'zh-CN', '用户']]);
  });

  it('keeps all four language states consistent after a scope failure and a superseded recovery', async () => {
    const { service, transloco } = await startInChinese();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const entered = service.loadScopes(['users']);
    await flushMicrotasks();
    loader.resolve('users/zh-CN', { title: '用户' });
    await entered;
    const changes = recordLangChanges(transloco);

    const failed = service.applyAccountLang('en');
    loader.resolve('en', { greeting: 'Hello' });
    await flushMicrotasks();
    loader.fail('users/en');
    await failed;
    expectActive(service, transloco, 'zh-CN', '你好');
    expect(transloco.translate('users.title')).toBe('用户');
    expect(changes).not.toContain('en');
    expect(errorHandler.handleError).toHaveBeenCalledTimes(1);

    // 同一路径连续失败仍是加载错误，不能因 Transloco 缓存了半初始化计数而变成 TypeError。
    await service.applyAccountLang('en');
    expectActive(service, transloco, 'zh-CN', '你好');
    expect(errorHandler.handleError).toHaveBeenCalledTimes(2);
    expect(
      errorHandler.handleError.mock.calls.every(([error]) => error instanceof TranslationLoadError),
    ).toBe(true);

    loader.restore('users/en');
    const superseded = service.applyAccountLang('en');
    await flushMicrotasks();
    await service.applyAccountLang('zh-CN');
    loader.resolve('users/en', { title: 'Users' });
    await superseded;
    expectActive(service, transloco, 'zh-CN', '你好');
    expect(transloco.translate('users.title')).toBe('用户');
    expect(changes).not.toContain('en');
  });

  it('waits for a scope entered while a language switch is loading', async () => {
    const { service, transloco } = await startInEnglish();
    const switched = service.applyAccountLang('zh-CN');
    const entered = service.loadScopes(['users']);
    await flushMicrotasks();
    loader.resolve('users/en', { title: 'Users' });
    await entered;
    loader.resolve('zh-CN', { greeting: '你好' });
    await flushMicrotasks();
    expect(await isSettled(switched)).toBe(false);
    loader.resolve('users/zh-CN', { title: '用户' });
    await switched;
    expectActive(service, transloco, 'zh-CN', '你好');
    expect(transloco.translate('users.title')).toBe('用户');
  });

  it('loads the final active language before completing a concurrent scope entry', async () => {
    const { service, transloco } = await startInEnglish();
    const entered = service.loadScopes(['users']);
    await flushMicrotasks();
    const switched = service.applyAccountLang('zh-CN');
    loader.resolve('zh-CN', { greeting: '你好' });
    await switched;
    loader.resolve('users/en', { title: 'Users' });
    await flushMicrotasks();
    expect(await isSettled(entered)).toBe(false);
    loader.resolve('users/zh-CN', { title: '用户' });
    expect(await entered).toBe(true);
    expectActive(service, transloco, 'zh-CN', '你好');
    expect(transloco.translate('users.title')).toBe('用户');
  });

  it('completes a repeated request for a language still loading only when its translations arrive', async () => {
    const { service } = await startInEnglish();

    const first = service.applyAccountLang('zh-CN');
    const second = service.applyAccountLang('zh-CN');

    expect(await isSettled(first)).toBe(false);
    expect(await isSettled(second)).toBe(false);

    loader.resolve('zh-CN', { greeting: '你好' });

    expect(await isSettled(first)).toBe(true);
    expect(await isSettled(second)).toBe(true);
    expect(service.activeLang()).toBe('zh-CN');
  });

  it('lets only the last of rapid switches take effect', async () => {
    const { service, transloco } = await startInEnglish();

    const superseded = service.applyAccountLang('zh-CN');
    // 中文词条还在路上，用户又切回英文：英文词条已就位，立即完成。
    await service.applyAccountLang('en');

    // 慢的旧请求晚到：已被取代，不能把语言切过去。
    loader.resolve('zh-CN', { greeting: '你好' });
    await superseded;

    expect(transloco.activeLang()).toBe('en');
    expect(service.activeLang()).toBe('en');
  });

  it('ignores a superseded switch that fails late', async () => {
    const { service, transloco } = await startInEnglish();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);

    const superseded = service.applyAccountLang('zh-CN');
    await service.applyAccountLang('en');

    loader.fail('zh-CN');
    await superseded;

    expect(transloco.activeLang()).toBe('en');
    expect(service.activeLang()).toBe('en');
  });

  it('stays on the previous language and reports the failure when translations cannot load', async () => {
    const { service, transloco } = await startInEnglish();
    // Transloco 在开发模式下会自己打一行加载失败日志；这里断言的是上报，不要那行噪声。
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const greeting = TestBed.runInInjectionContext(() => translateSignal('greeting'));

    const switched = service.applyAccountLang('zh-CN');
    loader.fail('zh-CN');
    await switched;

    // 切过去就停在没有词条的语言上，整页只剩空白与裸键
    expect(transloco.activeLang()).toBe('en');
    expect(service.activeLang()).toBe('en');
    expect(greeting()).toBe('Hello');
    expect(errorHandler.handleError).toHaveBeenCalledTimes(1);
  });

  // Transloco 默认在加载失败时转去加载回落语言并激活它；回落语言没加载过时，激活的是一份空词条
  it('keeps every view of the language on the previous one when the target fails and nothing else is loaded', async () => {
    const { service, transloco } = await startInChinese();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const changes = recordLangChanges(transloco);

    const switched = service.applyAccountLang('en');
    loader.fail('en');
    await switched;

    expectActive(service, transloco, 'zh-CN', '你好');
    // 途中也不能闪到失败的语言：订阅语言变化的地方会在那一刻按它重绘
    expect(changes).not.toContain('en');
    expect(errorHandler.handleError).toHaveBeenCalledTimes(1);
  });

  it('keeps the account language when the initial language fails after being superseded', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    configure();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const transloco = TestBed.inject(TranslocoService);
    const service = TestBed.inject(LanguageService);

    // 初始英文还在路上，账户中文先到并激活
    const account = service.applyAccountLang('zh-CN');
    loader.resolve('zh-CN', { greeting: '你好' });
    await account;
    expectActive(service, transloco, 'zh-CN', '你好');

    // 初始英文随后失败：已被取代，既不能被 Transloco 自行激活，也不能触发服务的退回
    loader.fail('en');
    await service.initialized;

    expectActive(service, transloco, 'zh-CN', '你好');
    expect(errorHandler.handleError).toHaveBeenCalledTimes(1);
  });

  // 切回的是还在加载的初始语言：不能当作已就位立即完成，它失败时也没有词条可停留
  it('falls back to the default language when switching back to an initial language that then fails', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'zh-CN');
    configure();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const transloco = TestBed.inject(TranslocoService);
    const service = TestBed.inject(LanguageService);

    void service.applyAccountLang('en');
    const back = service.resetToDeviceLang();
    expect(await isSettled(back)).toBe(false);

    loader.fail('zh-CN');
    loader.resolve('en', { greeting: 'Hello' });
    await back;

    expectActive(service, transloco, 'en', 'Hello');
  });

  // Transloco 在有过失败之后，把下一次成功加载的语言设为活动语言，不管这次加载是否已被取代
  it('ignores a retried language that succeeds after being superseded', async () => {
    const { service, transloco } = await startInChinese();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const failed = service.applyAccountLang('en');
    loader.fail('en');
    await failed;
    loader.restore('en');

    const superseded = service.applyAccountLang('en');
    await service.applyAccountLang('zh-CN');
    loader.resolve('en', { greeting: 'Hello' });
    await superseded;

    expectActive(service, transloco, 'zh-CN', '你好');
  });

  it('requests a failed language again when switching to it later', async () => {
    const { service, transloco } = await startInChinese();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const failed = service.applyAccountLang('en');
    loader.fail('en');
    await failed;
    loader.restore('en');
    const requestsBefore = loader.requests.length;

    const retried = service.applyAccountLang('en');
    expect(loader.requests.slice(requestsBefore)).toEqual(['en']);
    loader.resolve('en', { greeting: 'Hello' });
    await retried;

    expectActive(service, transloco, 'en', 'Hello');
  });

  // 失败后仍停在初始语言上：再次请求同一语言不能按"已是活动语言"短路，否则永远不重新加载
  it('requests the initial language again when it failed and is still the active one', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    configure([provideLanguageInitializer()]);
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const initStatus = TestBed.inject(ApplicationInitStatus);
    const service = TestBed.inject(LanguageService);
    const transloco = TestBed.inject(TranslocoService);
    loader.fail('en');
    await initStatus.donePromise;
    expect(transloco.translate('greeting')).toBe('greeting');
    loader.restore('en');
    const requestsBefore = loader.requests.length;

    const retried = service.applyAccountLang('en');
    await flushMicrotasks();
    expect(loader.requests.slice(requestsBefore)).toEqual(['en']);
    expect(await isSettled(retried)).toBe(false);
    loader.resolve('en', { greeting: 'Hello' });
    await retried;

    expectActive(service, transloco, 'en', 'Hello');
  });

  it('holds the first render until the initial language has loaded', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'zh-CN');
    configure([provideLanguageInitializer()]);
    const initStatus = TestBed.inject(ApplicationInitStatus);
    const transloco = TestBed.inject(TranslocoService);

    expect(transloco.activeLang()).toBe('zh-CN');
    expect(initStatus.done).toBe(false);

    loader.resolve('zh-CN', { greeting: '你好' });
    await initStatus.donePromise;

    expect(transloco.translate('greeting')).toBe('你好');
  });

  // 初始语言的词条取不到时仍要启动：初始化器一 reject，应用整个起不来，连启动失败页都没有
  it('falls back to the default language and still boots when the initial translations fail', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'zh-CN');
    configure([provideLanguageInitializer()]);
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const initStatus = TestBed.inject(ApplicationInitStatus);
    const transloco = TestBed.inject(TranslocoService);

    loader.fail('zh-CN');
    // 退回英语由服务在失败回调里发起，不是 Transloco 自己转去加载
    await flushMicrotasks();
    expect(loader.requests).toContain('en');
    loader.resolve('en', { greeting: 'Hello' });
    await initStatus.donePromise;

    expect(transloco.activeLang()).toBe('en');
    expect(transloco.translate('greeting')).toBe('Hello');
    expect(errorHandler.handleError).toHaveBeenCalledTimes(1);
  });
});
