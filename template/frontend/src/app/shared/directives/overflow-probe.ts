import {
  afterRenderEffect,
  Directive,
  DOCUMENT,
  ElementRef,
  inject,
  input,
  signal,
} from '@angular/core';

/**
 * 摘要被截断时才给展开入口：量宿主内标了 `data-clamp` 的元素（单行省略或限定行数）是否放不下。
 * 文字随字体就绪、容器尺寸与文案（切换语言）而变，三者变化时重量。
 * 展开后截断解除，量不出收起时放不放得下：`overflowProbeExpanded` 期间保留展开前的结论，收起后随即重量；
 * 以展开状态创建、还没量过时按截断算（展开只能从截断的入口进来）。收起入口因此不会在收起那一刻
 * 先消失再重建，焦点留在入口上。
 *
 * ```html
 * <span appOverflowProbe #summary="appOverflowProbe" [overflowProbeExpanded]="expanded">
 *   <span data-clamp [class.truncate]="!expanded">{{ title }}</span>
 * </span>
 * @if (summary.overflowing() || expanded) { <button [attr.aria-expanded]="expanded">…</button> }
 * ```
 */
@Directive({ selector: '[appOverflowProbe]', exportAs: 'appOverflowProbe' })
export class OverflowProbe {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly fontsLoaded = signal(false);
  private measured = false;

  /** 宿主内容已展开（截断解除）。 */
  readonly overflowProbeExpanded = input(false);

  /** 是否有被截断的部分；展开期间是展开前的结论。 */
  readonly overflowing = signal(false);

  constructor() {
    void inject(DOCUMENT).fonts?.ready.then(() => this.fontsLoaded.set(true));
    afterRenderEffect((onCleanup) => {
      this.fontsLoaded();
      if (this.overflowProbeExpanded()) {
        if (!this.measured) this.overflowing.set(true);
        return;
      }
      const measure = () => {
        this.measured = true;
        this.overflowing.set(
          Array.from(this.host.querySelectorAll<HTMLElement>('[data-clamp]')).some(
            (part) => part.scrollHeight > part.clientHeight || part.scrollWidth > part.clientWidth,
          ),
        );
      };
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
