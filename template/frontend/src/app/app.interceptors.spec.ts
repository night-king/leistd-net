import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationInitStatus, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TRANSLOCO_LOADER, TranslocoService } from '@jsverse/transloco';

import { appInterceptors } from './app.interceptors';
import { TranslocoHttpLoader } from './core/i18n/transloco-loader';
import { provideTranslocoTesting } from './core/i18n/transloco.testing';
import { LanguageService, provideLanguageInitializer } from './core/services/language-service';

/**
 * 真实启动链：语言初始化器经整条拦截器链（{@link appInterceptors}）与 HTTP 加载器取首帧词条。拦截器若在
 * 构造期注入依赖 LanguageService 的服务就会循环依赖（NG0200），应用停在启动页。
 */
describe('app interceptors', () => {
  afterEach(() => localStorage.removeItem(LanguageService.STORAGE_KEY));

  it('loads the initial translations through the whole chain without a construction cycle', async () => {
    localStorage.setItem(LanguageService.STORAGE_KEY, 'en');
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        ...provideTranslocoTesting(['en', 'zh-CN']),
        { provide: TRANSLOCO_LOADER, useClass: TranslocoHttpLoader },
        // 与应用同一条拦截器链，不手抄：后加的拦截器照样被覆盖到
        provideHttpClient(withInterceptors(appInterceptors)),
        provideHttpClientTesting(),
        provideRouter([]),
        provideLanguageInitializer(),
      ],
    });

    const init = TestBed.inject(ApplicationInitStatus);
    TestBed.inject(HttpTestingController)
      .expectOne((request) => request.url.endsWith('i18n/en.json'))
      .flush({ greeting: 'Hello' });
    await init.donePromise;

    expect(TestBed.inject(TranslocoService).translate('greeting')).toBe('Hello');
  });
});
