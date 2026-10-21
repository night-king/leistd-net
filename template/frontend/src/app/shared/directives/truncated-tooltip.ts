import {
  afterRenderEffect,
  computed,
  Directive,
  DOCUMENT,
  inject,
  input,
  signal,
} from '@angular/core';
import { BrnTooltip } from '@spartan-ng/brain/tooltip';

/**
 * 标签被截断时才提示全文：挂在带 `hlmTooltip` 的可聚焦元素上，传入做单行省略的文字元素；
 * 放得下时不提示，不重复可见文字。文字宽度随字体就绪、容器尺寸与文案（切换语言）而变，三者变化时重量，
 * 每次悬停或聚焦按最新结果决定是否提示；已经显示的提示照常在移开或失焦时收起。
 * 由本指令控制提示开关，同一元素上不再绑 `tooltipDisabled`。
 * 宿主本身不可聚焦时（如表格里的文字），经 `truncated()` 只在截断时加 `tabindex`，放得下就不多一个空的 Tab 停靠点。
 *
 * ```html
 * <a hlmCard [hlmTooltip]="label" [appTruncatedTooltip]="text">
 *   <span #text class="truncate">{{ label }}</span>
 * </a>
 * <span #id class="truncate" [hlmTooltip]="value" [appTruncatedTooltip]="id"
 *   #idTip="appTruncatedTooltip" [attr.tabindex]="idTip.truncated() ? 0 : null">{{ value }}</span>
 * ```
 */
@Directive({ selector: '[appTruncatedTooltip]', exportAs: 'appTruncatedTooltip' })
export class TruncatedTooltip {
  readonly appTruncatedTooltip = input.required<HTMLElement>();

  private readonly tooltip = inject(BrnTooltip);
  private readonly fontsLoaded = signal(false);

  /** 文字当前是否被截断（即提示是否启用）。 */
  readonly truncated = computed(() => !this.tooltip.mutableTooltipDisabled());

  constructor() {
    void inject(DOCUMENT).fonts?.ready.then(() => this.fontsLoaded.set(true));
    afterRenderEffect((onCleanup) => {
      this.fontsLoaded();
      const text = this.appTruncatedTooltip();
      const measure = () =>
        this.tooltip.mutableTooltipDisabled.set(text.scrollWidth <= text.clientWidth);
      measure();
      const resize = new ResizeObserver(measure);
      resize.observe(text);
      const mutation = new MutationObserver(measure);
      mutation.observe(text, { characterData: true, childList: true, subtree: true });
      onCleanup(() => {
        resize.disconnect();
        mutation.disconnect();
      });
    });
  }
}
