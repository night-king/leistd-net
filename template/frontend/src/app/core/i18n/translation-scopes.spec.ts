import { Component, ErrorHandler, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  provideTranslocoScope,
  TranslocoDirective,
  TranslocoService,
  translateObjectSignal,
  translateSignal,
  Translation,
  TranslocoLoader,
} from '@jsverse/transloco';
import { defer, of, Subject, throwError } from 'rxjs';

import { resolveTranslationScopes, TranslationScopeRecovery } from './translation-scopes';
import { provideTranslocoTesting } from './transloco.testing';
import { LanguageService } from '../services/language-service';

@Component({
  selector: 'app-scope-test-page',
  imports: [TranslocoDirective],
  template: `<ng-container *transloco="let t; prefix: 'users'">{{ t('title') }}</ng-container>`,
})
class ScopedPage {
  readonly immediate = inject(TranslocoService).translate('users.title');
  readonly title = translateSignal('users.title', {}, { scope: 'users' });
  readonly choices = translateObjectSignal(
    'users.choices',
    {},
    {
      scope: 'users',
    },
  );
}

@Component({ template: 'Home' })
class HomePage {}

@Component({
  selector: 'app-scope-test-aliased-page',
  imports: [TranslocoDirective],
  template: `<ng-container *transloco="let t; prefix: 'people'">{{ t('title') }}</ng-container>`,
})
class AliasedPage {
  readonly immediate = inject(TranslocoService).translate('people.title');
  readonly title = translateSignal('people.title');
  readonly choices = translateObjectSignal('people.choices');
}

class DelayedScopeLoader implements TranslocoLoader {
  readonly requests: string[] = [];
  readonly gates = new Map<string, Subject<Translation>>();
  private readonly unavailable = new Set<string>();

  getTranslation(path: string) {
    return defer(() => {
      this.requests.push(path);
      if (!path.includes('/')) {
        return of({ common: { save: path === 'en' ? 'Save' : '保存' } });
      }
      if (this.unavailable.has(path)) {
        return throwError(() => new Error('scope unavailable'));
      }
      const gate = new Subject<Translation>();
      this.gates.set(path, gate);
      return gate;
    });
  }

  fail(path: string): void {
    this.unavailable.add(path);
    this.gates.get(path)!.error(new Error('scope unavailable'));
  }

  restore(path: string): void {
    this.unavailable.delete(path);
  }

  resolve(path: string): void {
    const english = path.endsWith('/en');
    const gate = this.gates.get(path)!;
    gate.next({
      title: english ? 'Users' : '用户管理',
      choices: { active: english ? 'Active' : '启用' },
    });
    gate.complete();
  }
}

describe('translation scope routing', () => {
  afterEach(() => localStorage.removeItem(LanguageService.STORAGE_KEY));

  it('loads a deep-linked scope before constructing the page or caching any translation', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    const loader = new DelayedScopeLoader();
    TestBed.configureTestingModule({
      providers: [
        ...provideTranslocoTesting(['en', 'zh-CN'], loader),
        provideRouter([
          {
            path: 'users',
            providers: [provideTranslocoScope('users')],
            resolve: { translations: resolveTranslationScopes },
            component: ScopedPage,
          },
        ]),
      ],
    });
    await TestBed.inject(LanguageService).initialized;
    const harness = await RouterTestingHarness.create();
    const entered = harness.navigateByUrl('/users', ScopedPage);
    await vi.waitFor(() => expect(loader.requests).toContain('users/en'));
    expect(harness.routeNativeElement).toBeNull();

    loader.resolve('users/en');
    const page = await entered;
    harness.detectChanges();
    expect(page.immediate).toBe('Users');
    expect(page.title()).toBe('Users');
    expect(page.choices()).toEqual({ active: 'Active' });
    expect(harness.routeNativeElement!.textContent).toBe('Users');

    const switched = TestBed.inject(LanguageService).applyAccountLang('zh-CN');
    await vi.waitFor(() => expect(loader.requests).toContain('users/zh-CN'));
    harness.detectChanges();
    expect(harness.routeNativeElement!.textContent).toBe('Users');
    expect(page.title()).toBe('Users');
    expect(page.choices()).toEqual({ active: 'Active' });
    expect(TestBed.inject(TranslocoService).translate('users.title')).toBe('Users');

    loader.resolve('users/zh-CN');
    await switched;
    harness.detectChanges();
    expect(harness.routeNativeElement!.textContent).toBe('用户管理');
    expect(page.title()).toBe('用户管理');
    expect(page.choices()).toEqual({ active: '启用' });
    expect(TestBed.inject(TranslocoService).translate('users.title')).toBe('用户管理');
  });

  it('cancels scope entry on failure and preserves the current page and language', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    const loader = new DelayedScopeLoader();
    const errorHandler = { handleError: vi.fn() };
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      providers: [
        ...provideTranslocoTesting(['en', 'zh-CN'], loader),
        { provide: ErrorHandler, useValue: errorHandler },
        provideRouter([
          { path: '', component: HomePage },
          {
            path: 'users',
            providers: [provideTranslocoScope('users')],
            resolve: { translations: resolveTranslationScopes },
            component: ScopedPage,
          },
        ]),
      ],
    });
    const language = TestBed.inject(LanguageService);
    await language.initialized;
    const harness = await RouterTestingHarness.create('/');
    const entered = TestBed.inject(Router).navigateByUrl('/users');
    await vi.waitFor(() => expect(loader.requests).toContain('users/en'));
    loader.fail('users/en');
    expect(await entered).toBe(false);
    harness.detectChanges();
    expect(harness.routeNativeElement!.textContent).toBe('Home');
    expect(language.activeLang()).toBe('en');
    expect(TestBed.inject(TranslocoService).getActiveLang()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
    expect(TestBed.inject(TranslocoService).translate('common.save')).toBe('Save');
    expect(errorHandler.handleError).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(TranslationScopeRecovery).failedUrl()).toBeNull();
  });

  it('keeps the alias on first entry and when switching languages', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    const loader = new DelayedScopeLoader();
    TestBed.configureTestingModule({
      providers: [
        ...provideTranslocoTesting(['en', 'zh-CN'], loader),
        provideRouter([
          {
            path: 'people',
            component: AliasedPage,
            providers: [provideTranslocoScope({ scope: 'users', alias: 'people' })],
            resolve: { translations: resolveTranslationScopes },
          },
        ]),
      ],
    });
    const language = TestBed.inject(LanguageService);
    await language.initialized;
    const harness = await RouterTestingHarness.create();
    const entered = harness.navigateByUrl('/people', AliasedPage);
    await vi.waitFor(() => expect(loader.requests).toContain('users/en'));
    loader.resolve('users/en');
    const page = await entered;
    harness.detectChanges();
    expect(page.immediate).toBe('Users');
    expect(page.title()).toBe('Users');
    expect(page.choices()).toEqual({ active: 'Active' });
    expect(harness.routeNativeElement!.textContent).toBe('Users');
    const switched = language.applyAccountLang('zh-CN');
    await vi.waitFor(() => expect(loader.requests).toContain('users/zh-CN'));
    expect(TestBed.inject(TranslocoService).translate('people.title')).toBe('Users');
    loader.resolve('users/zh-CN');
    await switched;
    harness.detectChanges();
    expect(page.title()).toBe('用户管理');
    expect(page.choices()).toEqual({ active: '启用' });
    expect(harness.routeNativeElement!.textContent).toBe('用户管理');
    expect(TestBed.inject(TranslocoService).translate('people.title')).toBe('用户管理');
  });

  it('uses the registered inline loader on first entry and when switching languages', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    const http = {
      getTranslation: vi.fn((path: string) =>
        path.includes('/') ? throwError(() => new Error('unexpected HTTP scope load')) : of({}),
      ),
    };
    const en = vi.fn(async () => ({ default: { title: 'People', choices: { active: 'Active' } } }));
    const zh = vi.fn(async () => ({ title: '人员', choices: { active: '启用' } }));
    TestBed.configureTestingModule({
      providers: [
        ...provideTranslocoTesting(['en', 'zh-CN'], http),
        provideRouter([
          {
            path: 'people',
            component: AliasedPage,
            providers: [
              provideTranslocoScope({
                scope: 'users',
                alias: 'people',
                loader: { en, 'zh-CN': zh },
              }),
            ],
            resolve: { translations: resolveTranslationScopes },
          },
        ]),
      ],
    });
    const language = TestBed.inject(LanguageService);
    await language.initialized;
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl('/people', AliasedPage);
    harness.detectChanges();
    expect(page.immediate).toBe('People');
    expect(page.title()).toBe('People');
    expect(harness.routeNativeElement!.textContent).toBe('People');
    await language.applyAccountLang('zh-CN');
    harness.detectChanges();
    expect(page.title()).toBe('人员');
    expect(page.choices()).toEqual({ active: '启用' });
    expect(harness.routeNativeElement!.textContent).toBe('人员');
    expect(en).toHaveBeenCalledTimes(1);
    expect(zh).toHaveBeenCalledTimes(1);
    expect(http.getTranslation.mock.calls.filter(([path]) => path.includes('/'))).toEqual([]);
  });

  it('reexecutes a failed inline loader when retrying first scope entry', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    let unavailable = true;
    const en = vi.fn(async () => {
      if (unavailable) {
        throw new Error('inline scope unavailable');
      }
      return { default: { title: 'People', choices: { active: 'Active' } } };
    });
    const http = { getTranslation: vi.fn(() => of({ common: { save: 'Save' } })) };
    const errors = { handleError: vi.fn() };
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      providers: [
        ...provideTranslocoTesting(['en', 'zh-CN'], http),
        { provide: ErrorHandler, useValue: errors },
        provideRouter([
          {
            path: 'people',
            component: AliasedPage,
            providers: [provideTranslocoScope({ scope: 'users', alias: 'people', loader: { en } })],
            resolve: { translations: resolveTranslationScopes },
          },
        ]),
      ],
    });
    const language = TestBed.inject(LanguageService);
    await language.initialized;
    const harness = await RouterTestingHarness.create();
    const router = TestBed.inject(Router);
    const recovery = TestBed.inject(TranslationScopeRecovery);
    const url = '/people?source=inline#details';
    expect(await router.navigateByUrl(url)).toBe(false);
    expect(harness.routeNativeElement).toBeNull();
    expect(recovery.failedUrl()).toBe(url);
    expect(en).toHaveBeenCalledTimes(1);
    await recovery.retry();
    expect(recovery.failedUrl()).toBe(url);
    expect(en).toHaveBeenCalledTimes(2);
    unavailable = false;
    await recovery.retry();
    harness.detectChanges();
    const page = harness.routeDebugElement!.componentInstance as AliasedPage;
    expect(router.url).toBe(url);
    expect(recovery.failedUrl()).toBeNull();
    expect(en).toHaveBeenCalledTimes(3);
    expect(page.immediate).toBe('People');
    expect(page.title()).toBe('People');
    expect(page.choices()).toEqual({ active: 'Active' });
    expect(harness.routeNativeElement!.textContent).toBe('People');
    expect(language.activeLang()).toBe('en');
    expect(TestBed.inject(TranslocoService).getActiveLang()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
    expect(TestBed.inject(TranslocoService).translate('people.title')).toBe('People');
    expect(errors.handleError).toHaveBeenCalledTimes(2);
    expect(http.getTranslation.mock.calls).toHaveLength(1);
  });

  it('reexecutes a failed inline loader when retrying the target language', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    let unavailable = true;
    const en = vi.fn(async () => ({ title: 'People', choices: { active: 'Active' } }));
    const zh = vi.fn(async () => {
      if (unavailable) {
        throw new Error('inline scope unavailable');
      }
      return { default: { title: '人员', choices: { active: '启用' } } };
    });
    const http = {
      getTranslation: vi.fn((path: string) =>
        of({ common: { save: path === 'en' ? 'Save' : '保存' } }),
      ),
    };
    const errors = { handleError: vi.fn() };
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      providers: [
        ...provideTranslocoTesting(['en', 'zh-CN'], http),
        { provide: ErrorHandler, useValue: errors },
        provideRouter([
          {
            path: 'people',
            component: AliasedPage,
            providers: [
              provideTranslocoScope({
                scope: 'users',
                alias: 'people',
                loader: { en, 'zh-CN': zh },
              }),
            ],
            resolve: { translations: resolveTranslationScopes },
          },
        ]),
      ],
    });
    const language = TestBed.inject(LanguageService);
    await language.initialized;
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl('/people', AliasedPage);
    await language.applyAccountLang('zh-CN');
    await language.applyAccountLang('zh-CN');
    harness.detectChanges();
    expect(zh).toHaveBeenCalledTimes(2);
    expect(language.activeLang()).toBe('en');
    expect(TestBed.inject(TranslocoService).getActiveLang()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
    expect(TestBed.inject(TranslocoService).translate('people.title')).toBe('People');
    expect(page.title()).toBe('People');
    expect(harness.routeNativeElement!.textContent).toBe('People');
    unavailable = false;
    await language.applyAccountLang('zh-CN');
    harness.detectChanges();
    expect(zh).toHaveBeenCalledTimes(3);
    expect(en).toHaveBeenCalledTimes(1);
    expect(language.activeLang()).toBe('zh-CN');
    expect(TestBed.inject(TranslocoService).getActiveLang()).toBe('zh-CN');
    expect(document.documentElement.lang).toBe('zh-CN');
    expect(TestBed.inject(TranslocoService).translate('people.title')).toBe('人员');
    expect(page.title()).toBe('人员');
    expect(page.choices()).toEqual({ active: '启用' });
    expect(harness.routeNativeElement!.textContent).toBe('人员');
    expect(errors.handleError).toHaveBeenCalledTimes(2);
    expect(http.getTranslation.mock.calls.map(([path]) => path)).toEqual(['en', 'zh-CN']);
  });

  it('retries a rejected inline missing-key fallback without refetching successful resources', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'zh-CN');
    let unavailable = true;
    const en = vi.fn(async () => {
      if (unavailable) {
        throw new Error('inline fallback unavailable');
      }
      return { title: 'People', fallbackTitle: 'Fallback', choices: { active: 'Active' } };
    });
    const zh = vi.fn(async () => ({ title: '人员', choices: { active: '启用' } }));
    const http = { getTranslation: vi.fn(() => of({ common: { save: 'Save' } })) };
    TestBed.configureTestingModule({
      providers: [
        ...provideTranslocoTesting(['en', 'zh-CN'], http),
        { provide: ErrorHandler, useValue: { handleError: vi.fn() } },
        provideRouter([
          {
            path: 'people',
            component: AliasedPage,
            providers: [
              provideTranslocoScope({
                scope: 'users',
                alias: 'people',
                loader: { en, 'zh-CN': zh },
              }),
            ],
            resolve: { translations: resolveTranslationScopes },
          },
        ]),
      ],
    });
    const transloco = TestBed.inject(TranslocoService);
    transloco.config.missingHandler.useFallbackTranslation = true;
    transloco.setFallbackLangForMissingTranslation({ fallbackLang: 'en' });
    const language = TestBed.inject(LanguageService);
    await language.initialized;
    const harness = await RouterTestingHarness.create();
    const router = TestBed.inject(Router);
    expect(await router.navigateByUrl('/people')).toBe(false);
    expect(harness.routeNativeElement).toBeNull();
    expect(zh).toHaveBeenCalledTimes(1);
    expect(en).toHaveBeenCalledTimes(1);
    unavailable = false;
    await TestBed.inject(TranslationScopeRecovery).retry();
    harness.detectChanges();
    expect(en).toHaveBeenCalledTimes(2);
    expect(zh).toHaveBeenCalledTimes(1);
    expect(harness.routeNativeElement!.textContent).toBe('人员');
    expect(transloco.translate('people.fallbackTitle')).toBe('Fallback');
    expect(language.activeLang()).toBe('zh-CN');
    expect(transloco.getActiveLang()).toBe('zh-CN');
    expect(document.documentElement.lang).toBe('zh-CN');
  });
});
