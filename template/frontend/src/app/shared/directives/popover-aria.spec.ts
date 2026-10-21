import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { PopoverAria } from './popover-aria';

/**
 * 可访问名必须落在 CDK overlay pane 上（Brain 把 `role="dialog"` 设在那里），
 * 写在内容元素自己身上读屏读不到。这里用一个带 pane 类名的容器代替真实浮层。
 */
@Component({
  imports: [PopoverAria],
  template: `
    <div class="cdk-overlay-pane" role="dialog" data-testid="pane">
      <div [appPopoverAria]="label()" data-testid="content"></div>
    </div>
    <div [appPopoverAria]="label()" data-testid="detached"></div>
  `,
})
class HostComponent {
  readonly label = signal('Notifications');
}

describe('PopoverAria', () => {
  function render() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const byTestId = (id: string) => element.querySelector<HTMLElement>(`[data-testid="${id}"]`)!;
    return {
      fixture,
      pane: byTestId('pane'),
      content: byTestId('content'),
      detached: byTestId('detached'),
    };
  }

  it('names the overlay pane that owns the content, not the content itself', () => {
    const { pane, content } = render();

    expect(pane.getAttribute('aria-label')).toBe('Notifications');
    expect(content.hasAttribute('aria-label')).toBe(false);
  });

  it('follows label changes such as a language switch', () => {
    const { fixture, pane } = render();

    fixture.componentInstance.label.set('通知');
    fixture.detectChanges();

    expect(pane.getAttribute('aria-label')).toBe('通知');
  });

  it('keeps the last name when the label becomes empty', () => {
    const { fixture, pane } = render();

    fixture.componentInstance.label.set('');
    fixture.detectChanges();

    expect(pane.getAttribute('aria-label')).toBe('Notifications');
  });

  it('does nothing outside an overlay pane', () => {
    const { detached } = render();

    expect(detached.hasAttribute('aria-label')).toBe(false);
    expect(
      document.querySelector('[aria-label="Notifications"]:not(.cdk-overlay-pane)'),
    ).toBeNull();
  });
});
