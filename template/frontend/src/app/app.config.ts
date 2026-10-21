import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  ErrorHandler,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import {
  provideRouter,
  RouterFeatures,
  withComponentInputBinding,
  //#if (LocalIdentity)
  withHashLocation,
  //#endif
  withInMemoryScrolling,
  withViewTransitions,
} from '@angular/router';
//#if (IncludeLocalization)
import { provideTransloco } from '@jsverse/transloco';
//#endif
import { provideHlmSidebarConfig } from '@spartan-ng/helm/sidebar';
import { provideSpartanHlm } from '@spartan-ng/helm/utils';

import { appInterceptors } from './app.interceptors';
import { routes } from './app.routes';
import { environment } from '../environments/environment';
import { GlobalErrorHandler } from './core/handlers/global-error-handler';
//#if (IncludeLocalization)
import { provideAppA11yLabels } from './core/i18n/a11y-labels';
import { TranslocoHttpLoader } from './core/i18n/transloco-loader';
import {
  DEFAULT_LANG,
  provideLanguageFallbackStrategy,
  provideLanguageInitializer,
  SUPPORTED_LANGS,
} from './core/services/language-service';
//#endif
import { StartupService } from './core/services/startup-service';
import { provideAppCalendarLocale } from './core/settings/calendar-locale';
import { provideMock } from '../../_mock/core/providers';

const routerFeatures: RouterFeatures[] = [
  withComponentInputBinding(),
  withViewTransitions(),
  withInMemoryScrolling({ anchorScrolling: 'enabled', scrollPositionRestoration: 'enabled' }),
  //#if (LocalIdentity)
  ...(environment.useHash ? [withHashLocation()] : []),
  //#endif
];

export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    // Spartan：Angular 21+ 需注册，确保 CDK overlay 层级正确（避免盖过固定定位的 toaster）
    provideSpartanHlm(),
    // 移动端点击侧栏菜单项（导航）后自动收起遮罩侧栏；非导航的下拉触发器单独关闭该行为。
    provideHlmSidebarConfig({ closeMobileSidebarOnMenuButtonClick: true }),
    provideBrowserGlobalErrorListeners(),
    { provide: ErrorHandler, useClass: GlobalErrorHandler },
    provideRouter(routes, ...routerFeatures),
    //#if (IncludeLocalization)
    provideTransloco({
      config: {
        availableLangs: [...SUPPORTED_LANGS],
        defaultLang: DEFAULT_LANG,
        fallbackLang: DEFAULT_LANG,
        reRenderOnLangChange: true,
        scopes: { autoPrefixKeys: false },
        prodMode: environment.production,
        // 生产构建的词条已由 postbuild 的 transloco-optimize 预先展平，运行时不必再展平一遍。
        // 前提是生产构建走 npm run build（它才会触发 postbuild）；直接 ng build 出来的是未展平的原文件，词条会全部找不到
        flatten: { aot: environment.production },
      },
      loader: TranslocoHttpLoader,
    }),
    // 加载失败不让 Transloco 自行回落并激活，由 LanguageService 处理（理由见 provideLanguageFallbackStrategy）
    provideLanguageFallbackStrategy(),
    provideAppA11yLabels(),
    //#endif
    provideHttpClient(withInterceptors(appInterceptors)),
    // 日期选择器的星期、月份与翻月读屏名跟随界面写法（理由见 provideAppCalendarLocale）
    provideAppCalendarLocale(),
    //#if (IncludeLocalization)
    // 首帧前加载活动语言词条，首次渲染不出裸键（理由见 provideLanguageInitializer）。
    provideLanguageInitializer(),
    //#endif
    provideAppInitializer(() => inject(StartupService).load()),
    ...provideMock(environment.useMock),
  ],
};
