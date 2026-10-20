import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { OverflowProbe } from './overflow-probe';

@Component({
  imports: [OverflowProbe],
  template: `
    <div
      appOverflowProbe
      #probe="appOverflowProbe"
      class="flex flex-col"
      [style.width.px]="width()"
    >
      <span data-clamp [class.truncate]="!expanded()">{{ title() }}</span>
      <span data-clamp [class.line-clamp-2]="!expanded()">{{ body() }}</span>
    </div>
    <output>{{ probe.overflowing() }}</output>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly width = signal(600);
  readonly expanded = signal(false);
  readonly title = signal('Short title');
  readonly body = signal('Short body.');
}

/** 单行省略与限定行数都算截断；宽度、文案与展开状态变化后重新判断。 */
describe('OverflowProbe', () => {
  let fixture: ComponentFixture<Host>;

  const overflowing = () =>
    (fixture.nativeElement as HTMLElement).querySelector('output')!.textContent;

  beforeEach(async () => {
    fixture = TestBed.createComponent(Host);
    await settle();
  });

  async function settle(): Promise<void> {
    fixture.detectChanges();
    // ResizeObserver 与 MutationObserver 在下一帧前回调
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('reports nothing clipped when the text fits', () => {
    expect(overflowing()).toBe('false');
  });

  it('reports a single-line title cut off by a narrower container', async () => {
    fixture.componentInstance.title.set('A considerably longer title that cannot fit');
    fixture.componentInstance.width.set(80);
    await settle();

    expect(overflowing()).toBe('true');
  });

  it('reports a body longer than its line limit, and clears once expanded', async () => {
    fixture.componentInstance.body.set('Three entries could not be matched. '.repeat(20));
    await settle();
    expect(overflowing()).toBe('true');

    fixture.componentInstance.expanded.set(true);
    await settle();
    expect(overflowing()).toBe('false');
  });
});
