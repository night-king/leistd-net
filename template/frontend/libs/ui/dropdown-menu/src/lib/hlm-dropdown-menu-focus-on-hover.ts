import { CdkMenu, CdkMenuItem } from '@angular/cdk/menu';
import { Directive, ElementRef, inject } from '@angular/core';

/**
 * @internal
 * Moves DOM focus to the hovered menu item. CDK menus only move focus with the keyboard, so on a pointer
 * the highlight would otherwise stay on the last keyboard-focused item (leaving two rows highlighted) and,
 * when a submenu closes, focus would fall to <body>, the menu stack would report no focus, and the whole
 * dropdown would collapse. Following the pointer with focus (Radix/shadcn behaviour) keeps a single
 * highlight and keeps focus inside the menu stack. setActiveMenuItem also syncs the key manager so keyboard
 * navigation continues from the hovered item.
 *
 * Follows pointer movement rather than mouseenter, as Radix does: browsers also fire mouseenter when a menu
 * opens under a resting cursor, which is not a hover and would pull focus away from the first item that
 * keyboard opening just focused. Touch drags do not move focus.
 *
 * Applied as a host directive on every dropdown item type (item, checkbox, radio, sub-trigger).
 */
@Directive({
  selector: '[hlmDropdownMenuFocusOnHover]',
  exportAs: 'hlmDropdownMenuFocusOnHover',
  host: {
    '(pointermove)': '_focusOnHover($event)',
  },
})
export class HlmDropdownMenuFocusOnHover {
  private readonly _cdkMenuItem = inject(CdkMenuItem, { self: true });
  private readonly _parentMenu = inject(CdkMenu, { optional: true });
  private readonly _element = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  protected _focusOnHover(event: PointerEvent): void {
    if (
      event.pointerType === 'touch' ||
      this._cdkMenuItem.disabled ||
      this._element.ownerDocument.activeElement === this._element
    ) {
      return;
    }
    this._parentMenu?.setActiveMenuItem(this._cdkMenuItem);
  }
}
