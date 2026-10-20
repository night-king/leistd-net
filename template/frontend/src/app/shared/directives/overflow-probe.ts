import { afterRenderEffect, Directive, DOCUMENT, ElementRef, inject, signal } from '@angular/core';

/**
 * 摘要被截断时才给展开入口：量宿主内标了 `data-clamp` 的元素（单行省略或限定行数）是否放不下。
 * 文字随字体就绪、容器尺寸（含展开收起）与文案（切换语言）而变，三者变化时重量。
 * 展开后元素不再截断，`overflowing()` 随之为 false，使用方按"截断或已展开"决定是否保留收起入口。
 *
 * ```html
 * <span appOverflowProbe #summary="appOverflowProbe">
 *   <span data-clamp [class.truncate]="!expanded">{{ title }}</span>
 * </span>
 * @if (summary.overflowing() || expanded) { <button [attr.aria-expanded]="expanded">…</button> }
 * ```
 */
@Directive({ selector: '[appOverflowProbe]', exportAs: 'appOverflowProbe' })
export class OverflowProbe {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly fontsLoaded = signal(false);

  /** 是否有被截断的部分。 */
  readonly overflowing = signal(false);

  constructor() {
    void inject(DOCUMENT).fonts?.ready.then(() => this.fontsLoaded.set(true));
    afterRenderEffect((onCleanup) => {
      this.fontsLoaded();
      const measure = () =>
        this.overflowing.set(
          Array.from(this.host.querySelectorAll<HTMLElement>('[data-clamp]')).some(
            (part) => part.scrollHeight > part.clientHeight || part.scrollWidth > part.clientWidth,
          ),
        );
      measure();
      const resize = new ResizeObserver(measure);
      resize.observe(this.host);
      const mutation = new MutationObserver(measure);
      mutation.observe(this.host, { characterData: true, childList: true, subtree: true });
      onCleanup(() => {
        resize.disconnect();
        mutation.disconnect();
      });
    });
  }
}
