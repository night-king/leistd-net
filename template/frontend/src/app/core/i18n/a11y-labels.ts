import { inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoService } from '@jsverse/transloco';
import { provideHlmA11yLabels } from '@spartan-ng/helm/utils';

/**
 * 把 `libs/ui` 组件内部的无障碍文案（对话框关闭按钮、侧栏折叠开关的读屏名）接上译文。单独成文件以便
 * 测试：上游升级可能让组件不再读令牌，守卫见 `a11y-labels.spec.ts`。
 */
export function provideAppA11yLabels() {
  return provideHlmA11yLabels(() => {
    const transloco = inject(TranslocoService);
    return {
      // toSignal 包住 selectTranslate 让它随语言切换重算——静态字符串会固定在启动时的语言上
      close: toSignal(transloco.selectTranslate<string>('common.close'), {
        initialValue: 'Close',
      }),
      toggleSidebar: toSignal(transloco.selectTranslate<string>('layout.sidebar.toggle'), {
        initialValue: 'Toggle Sidebar',
      }),
    };
  });
}
