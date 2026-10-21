import { inject, InjectionToken, signal, type FactoryProvider, type Signal } from '@angular/core';

/**
 * 组件内部渲染、宿主接触不到的无障碍文案（屏幕阅读器专用）。
 *
 * 这些文案写在组件模板里，页面上没有任何地方能传进去；不集中到这里的话，多语言项目里
 * 整页中文只有关闭按钮被读成英文，而且每新增一个对话框就多一处漏网。
 *
 * 值是 `Signal` 而不是字符串：译文异步加载、且要跟随语言切换重算，静态值会被固定在
 * 应用启动那一刻的语言上。
 */
export type HlmA11yLabels = {
  /** 对话框 / 抽屉右上角关闭按钮 */
  close: Signal<string>;
  /** 侧边栏折叠开关 */
  toggleSidebar: Signal<string>;
};

// 默认英文：libs/ui 不依赖任何本地化库，单独使用这些组件时也要能读出意义
const defaultLabels: HlmA11yLabels = {
  close: signal('Close'),
  toggleSidebar: signal('Toggle Sidebar'),
};

const HlmA11yLabelsToken = new InjectionToken<HlmA11yLabels>('HlmA11yLabels');

/**
 * 收工厂而不是现成的值：译文信号通常要 `inject(TranslocoService)` 才能建，
 * 而 providers 数组求值时还不在注入上下文里。工厂在容器创建该令牌时才跑，那时可以 inject。
 */
export function provideHlmA11yLabels(labelsFactory: () => Partial<HlmA11yLabels>): FactoryProvider {
  return {
    provide: HlmA11yLabelsToken,
    useFactory: () => ({ ...defaultLabels, ...labelsFactory() }),
  };
}

export function injectHlmA11yLabels(): HlmA11yLabels {
  return inject(HlmA11yLabelsToken, { optional: true }) ?? defaultLabels;
}
