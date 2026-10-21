import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HlmButtonImports } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { userEvent } from 'vitest/browser';

/**
 * 下拉菜单项随指针移焦点（helm 定制，见 docs/standards/frontend-spartan.md）：只跟随真实移动的鼠标，
 * 菜单弹在静止光标下、触屏拖动都不抢键盘焦点；指针从子菜单回到父菜单时只收起子菜单，焦点留在父菜单。
 */
@Component({
  selector: 'app-test-host',
  imports: [HlmButtonImports, HlmDropdownMenuImports],
  template: `
    <button hlmBtn variant="outline" data-testid="trigger" [hlmDropdownMenuTrigger]="menu">
      Open
    </button>
    <ng-template #menu>
      <hlm-dropdown-menu class="w-48">
        <button hlmDropdownMenuItem>Profile</button>
        <button hlmDropdownMenuItem side="right" align="start" [hlmDropdownMenuSubTrigger]="sub">
          More
          <hlm-dropdown-menu-item-sub-indicator />
        </button>
        <button hlmDropdownMenuItem>Billing</button>
      </hlm-dropdown-menu>
    </ng-template>
    <ng-template #sub>
      <hlm-dropdown-menu-sub class="w-40">
        <button hlmDropdownMenuItem>Email</button>
        <button hlmDropdownMenuItem>Message</button>
      </hlm-dropdown-menu-sub>
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class TestHost {}

describe('dropdown menu hover focus', () => {
  async function settle(fixture: { whenStable: () => Promise<unknown> }): Promise<void> {
    await fixture.whenStable();
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
  }

  /** 按文案取当前打开的菜单项（浮层挂在 document 上）。 */
  function item(label: string): HTMLElement {
    const found = Array.from(document.querySelectorAll<HTMLElement>('[hlmDropdownMenuItem]')).find(
      (element) => element.textContent?.trim() === label,
    );
    expect(found, `menu item ${label}`).toBeDefined();
    return found!;
  }

  async function open() {
    const fixture = TestBed.createComponent(TestHost);
    await fixture.whenStable();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLElement>('[data-testid="trigger"]')!
      .click();
    await settle(fixture);
    return fixture;
  }

  it('closes only the submenu when the pointer moves back to the parent menu', async () => {
    const fixture = await open();
    await userEvent.hover(item('More'));
    await settle(fixture);
    await userEvent.hover(item('Email'));
    await settle(fixture);
    expect(document.activeElement).toBe(item('Email'));

    await userEvent.hover(item('Billing'));
    await settle(fixture);

    expect(document.querySelector('hlm-dropdown-menu-sub')).toBeNull();
    expect(document.querySelector('hlm-dropdown-menu')).not.toBeNull();
    expect(document.activeElement).toBe(item('Billing'));
    // 键盘从指针停留的那一项接着走
    await userEvent.keyboard('{ArrowUp}');
    expect(document.activeElement).toBe(item('More'));
  });

  it('does not move focus on a touch drag across the menu', async () => {
    const fixture = await open();
    item('Profile').focus();

    item('Billing').dispatchEvent(
      new PointerEvent('pointermove', { pointerType: 'touch', bubbles: true }),
    );
    await settle(fixture);
    expect(document.activeElement).toBe(item('Profile'));

    item('Billing').dispatchEvent(
      new PointerEvent('pointermove', { pointerType: 'mouse', bubbles: true }),
    );
    await settle(fixture);
    expect(document.activeElement).toBe(item('Billing'));
  });
});
