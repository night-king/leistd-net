import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HlmCheckboxImports } from '@spartan-ng/helm/checkbox';
import { HlmRadioGroupImports } from '@spartan-ng/helm/radio-group';
import { HlmSwitchImports } from '@spartan-ng/helm/switch';
import { HlmTableImports } from '@spartan-ng/helm/table';

import {
  expectTouchTarget,
  MIN_TOUCH_TARGET_PX,
  setCoarsePointer,
  tapAt,
} from './coarse-pointer.testing';

/** 三种视觉尺寸小于 44px 的控件各放一个可用、一个禁用，彼此隔开，命中区不互相覆盖。 */
@Component({
  selector: 'app-test-host',
  imports: [HlmCheckboxImports, HlmSwitchImports, HlmRadioGroupImports],
  template: `
    <div class="flex flex-col items-start gap-12 p-12">
      <hlm-checkbox
        aria-label="checkbox"
        [checked]="checkbox()"
        (checkedChange)="checkbox.set($event)"
      />
      <hlm-checkbox aria-label="disabled checkbox" disabled />
      <hlm-switch aria-label="switch" [checked]="toggle()" (checkedChange)="toggle.set($event)" />
      <hlm-switch aria-label="disabled switch" disabled />
      <hlm-radio-group
        class="flex flex-col gap-12"
        [value]="choice()"
        (valueChange)="choice.set($event)"
      >
        <hlm-radio value="a" aria-label="a"><hlm-radio-indicator indicator /></hlm-radio>
        <hlm-radio value="b" aria-label="b" disabled><hlm-radio-indicator indicator /></hlm-radio>
      </hlm-radio-group>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class TestHost {
  readonly checkbox = signal(false);
  readonly toggle = signal(false);
  readonly choice = signal<string | null>(null);
}

/**
 * 表头里的复选框：外层像列表页一样裁掉溢出。第一张表只有复选框，表头高度全靠触屏规则；
 * 第二张表的选择列不定宽、紧贴内容，右邻单元格放一个 44px 按钮，复选框的命中区不能伸进它。
 */
@Component({
  selector: 'app-table-host',
  imports: [HlmCheckboxImports, HlmTableImports],
  template: `
    <div class="flex flex-col gap-12 p-12">
      <div class="overflow-hidden" data-testid="bare">
        <table hlmTable>
          <thead hlmTHead>
            <tr hlmTr>
              <th hlmTh class="w-10 ps-4"><hlm-checkbox aria-label="all" /></th>
              <th hlmTh>Name</th>
            </tr>
          </thead>
        </table>
      </div>
      <div class="overflow-hidden" data-testid="neighbour">
        <table hlmTable class="w-auto">
          <thead hlmTHead>
            <tr hlmTr>
              <th hlmTh class="ps-4"><hlm-checkbox aria-label="all" /></th>
              <th hlmTh><button type="button" class="size-11">Name</button></th>
            </tr>
          </thead>
        </table>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class TableHost {}

/**
 * 弹窗里常见的滚动列表：外层有内边距，滚动区（`overflow-y-auto`）却紧贴首列。滚动区会裁掉伸出它的命中区，
 * 外层的内边距补不上；第二个列表在滚动区内部给行留了起始内边距。两行纵向都留足了空间，只差起始内边距。
 */
@Component({
  selector: 'app-scroll-host',
  imports: [HlmCheckboxImports],
  template: `
    <div class="flex flex-col gap-12 p-12">
      <div class="max-h-40 overflow-y-auto" data-testid="flush">
        <div class="flex items-center gap-3 py-3.5">
          <hlm-checkbox aria-label="flush" />
          <span>Flush</span>
        </div>
      </div>
      <div class="max-h-40 overflow-y-auto" data-testid="padded">
        <div class="flex items-center gap-3 py-3.5 ps-3.5">
          <hlm-checkbox aria-label="padded" />
          <span>Padded</span>
        </div>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class ScrollHost {}

describe('small controls on coarse pointers', () => {
  afterEach(async () => {
    await setCoarsePointer(false);
  });

  async function render() {
    const fixture = TestBed.createComponent(TestHost);
    await fixture.whenStable();
    return fixture;
  }

  function controls(root: HTMLElement) {
    const [checkbox, disabledCheckbox] = root.querySelectorAll('[role="checkbox"]');
    const [toggle, disabledToggle] = root.querySelectorAll('[role="switch"]');
    const [indicator, disabledIndicator] = root.querySelectorAll(
      '[data-slot="radio-group-indicator"]',
    );
    return { checkbox, disabledCheckbox, toggle, disabledToggle, indicator, disabledIndicator };
  }

  // 视觉方框之外、44px 命中区之内的一点：从中心往右下各偏 20px
  const EDGE = MIN_TOUCH_TARGET_PX / 2 - 2;

  it('keeps the visual size and has no hit area on fine pointers', async () => {
    const fixture = await render();
    const { checkbox, toggle, indicator } = controls(fixture.nativeElement);

    expect(checkbox.getBoundingClientRect().width).toBe(16);
    expect(indicator.getBoundingClientRect().width).toBe(16);
    expect(toggle.getBoundingClientRect().width).toBeLessThan(MIN_TOUCH_TARGET_PX);
    expect(getComputedStyle(checkbox, '::after').content).toBe('none');
  });

  it('spreads each control to a 44px target without changing its visual size', async () => {
    await setCoarsePointer(true);
    const fixture = await render();
    const { checkbox, toggle, indicator } = controls(fixture.nativeElement);

    expect(checkbox.getBoundingClientRect().width).toBe(16);
    for (const element of [checkbox, toggle, indicator]) {
      expectTouchTarget(element);
    }
  });

  it('toggles each control when tapped outside its visual box', async () => {
    await setCoarsePointer(true);
    const fixture = await render();
    const host = fixture.componentInstance;
    const { checkbox, toggle, indicator } = controls(fixture.nativeElement);

    await tapAt(checkbox, EDGE, EDGE);
    await tapAt(toggle, 0, EDGE);
    await tapAt(indicator, -EDGE, -EDGE);
    await fixture.whenStable();

    expect(host.checkbox()).toBe(true);
    expect(host.toggle()).toBe(true);
    expect(host.choice()).toBe('a');
  });

  it('keeps disabled controls unchanged when their hit area is tapped', async () => {
    await setCoarsePointer(true);
    const fixture = await render();
    const host = fixture.componentInstance;
    const { disabledCheckbox, disabledToggle, disabledIndicator } = controls(fixture.nativeElement);

    await tapAt(disabledCheckbox, EDGE, EDGE, { disabled: true });
    await tapAt(disabledToggle, 0, EDGE, { disabled: true });
    await tapAt(disabledIndicator, EDGE, EDGE, { disabled: true });
    await fixture.whenStable();

    expect(disabledCheckbox.getAttribute('aria-checked')).toBe('false');
    expect(disabledToggle.getAttribute('aria-checked')).toBe('false');
    expect(host.choice()).toBeNull();
  });

  it('gives a header checkbox room above, below and beside it inside a clipped table', async () => {
    await setCoarsePointer(true);
    const fixture = TestBed.createComponent(TableHost);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const bare = root.querySelector('[data-testid="bare"] [role="checkbox"]')!;
    const neighbour = root.querySelector('[data-testid="neighbour"]')!;

    expectTouchTarget(bare);
    expectTouchTarget(neighbour.querySelector('[role="checkbox"]')!);
    expectTouchTarget(neighbour.querySelector('button:not([role])')!);
  });

  // 滚动区紧贴首列时命中区左沿被裁：外层留白救不了，助手必须能查出来；在滚动区内部留起始内边距后通过
  it('detects a hit area clipped by a scroll container and passes once the container leaves room', async () => {
    await setCoarsePointer(true);
    const fixture = TestBed.createComponent(ScrollHost);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;

    expect(() =>
      expectTouchTarget(root.querySelector('[data-testid="flush"] [role="checkbox"]')!),
    ).toThrow();
    expectTouchTarget(root.querySelector('[data-testid="padded"] [role="checkbox"]')!);
  });
});
