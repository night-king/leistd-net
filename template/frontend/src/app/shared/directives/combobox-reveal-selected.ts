import {
  Directive,
  ElementRef,
  afterNextRender,
  contentChildren,
  inject,
  input,
} from '@angular/core';
import { BrnComboboxItem, injectBrnComboboxBase } from '@spartan-ng/brain/combobox';

/**
 * 组合框展开时把当前值滚到列表中间并高亮，方向键从这里接着走。Brain 只在输入搜索后高亮第一项，
 * 展开时停在列表顶部，几百项的列表里看不到当前值；Spartan 原生支持后可移除。
 * 没有选中值时（例如继承来的值只作占位）定位到 `fallback`。
 * 用法：`<div hlmComboboxList [appComboboxRevealSelected]="inheritedValue">`，放在 `*hlmComboboxPortal`
 * 内，每次展开重新创建，因此每次展开都会定位。
 */
@Directive({
  selector: '[appComboboxRevealSelected]',
})
export class ComboboxRevealSelected {
  /** 没有选中值时要定位的值；空串表示不定位。 */
  readonly fallback = input<unknown>('', { alias: 'appComboboxRevealSelected' });

  private readonly combobox = injectBrnComboboxBase<unknown>();
  private readonly items = contentChildren(BrnComboboxItem, { descendants: true });
  private readonly host = inject(ElementRef<HTMLElement>);

  constructor() {
    afterNextRender(() => {
      const fallback = this.fallback();
      const item =
        this.items().find((i) => this.combobox.isSelected(i.value())) ??
        (fallback === '' ? undefined : this.items().find((i) => i.value() === fallback));
      if (!item) {
        return;
      }

      // 只滚列表自己：scrollIntoView 会连带滚动外层页面。位置用 offsetTop 算：展开动画期间面板带缩放，
      // getBoundingClientRect 量到的距离按比例偏小，长列表里能差出好几屏。
      const list = this.host.nativeElement as HTMLElement;
      const element = document.getElementById(item.id());
      if (element) {
        const offset = element.offsetTop - list.offsetTop;
        list.scrollTop = offset - (list.clientHeight - element.offsetHeight) / 2;
      }
      this.combobox.keyManager.setActiveItem(item);
    });
  }
}
