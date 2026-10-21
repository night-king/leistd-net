import { EnvironmentProviders, Provider } from '@angular/core';
import { provideTransloco, TRANSLOCO_LOADER, TranslocoLoader } from '@jsverse/transloco';
import { of } from 'rxjs';

import { provideLanguageFallbackStrategy } from '../services/language-service';

/** 空词条：文案回落成键名，那正是用例里能安全断言的东西。 */
const emptyTranslations: TranslocoLoader = { getTranslation: () => of({}) };

/**
 * 单测用的 Transloco 装配。词条一律为空、文案回落成键名：组件用例断言行为而不是文案，
 * 也免去异步加载。缺词条日志同时关掉，词条完整性由 i18n 静态闸门与浏览器闭环负责。
 * 不手写 `TranslocoService` 桩：结构指令与 `translateSignal()` 依赖的配置与事件流难以补齐。
 *
 * 页面级用例若在首次渲染途中才创建 `LanguageService`（经 `SettingContextService` 等间接依赖），
 * 结构指令会在渲染中重入并报 "max number of directives"；在 `createComponent` 之前先注入它
 * （或依赖它的服务）。这里不统一预建：它会读本机语言偏好，让失败随环境变化。
 *
 * @param langs 可用语言，第一个同时作为默认与回落语言。
 * @param loader 需要控制词条到达时机的用例传自己的加载器（见 language-service.spec）。
 */
export function provideTranslocoTesting(
  langs: readonly [string, ...string[]] = ['en'],
  loader: TranslocoLoader = emptyTranslations,
): (Provider | EnvironmentProviders)[] {
  return [
    provideTransloco({
      config: {
        availableLangs: [...langs],
        defaultLang: langs[0],
        fallbackLang: langs[0],
        // 与应用配置一致：关掉时结构指令只取第一次的语言，切换语言的用例就测不出任何东西
        reRenderOnLangChange: true,
        scopes: { autoPrefixKeys: false },
        missingHandler: { logMissingKey: false },
      },
    }),
    { provide: TRANSLOCO_LOADER, useValue: loader },
    // 与应用配置一致：加载失败的处理归 LanguageService，用默认策略就测不到应用里的失败路径
    provideLanguageFallbackStrategy(),
  ];
}
