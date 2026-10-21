import { ComponentFixture, TestBed } from '@angular/core/testing';

import { FacetedFilter, FacetedFilterOption } from './faceted-filter';

describe('FacetedFilter', () => {
  const OPTIONS: FacetedFilterOption[] = [
    { value: 'admin', label: 'Administrator' },
    { value: 'member', label: 'Member' },
    { value: 'guest', label: 'Guest' },
  ];

  beforeEach(() => TestBed.configureTestingModule({ imports: [FacetedFilter] }));

  function createFilter(inputs: Record<string, unknown>): ComponentFixture<FacetedFilter> {
    const fixture = TestBed.createComponent(FacetedFilter);
    fixture.componentRef.setInput('label', 'Role');
    fixture.componentRef.setInput('options', OPTIONS);
    for (const [key, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(key, value);
    }
    fixture.detectChanges();
    fixture.componentInstance.state.set('open');
    fixture.detectChanges();
    return fixture;
  }

  const listbox = () => document.querySelector<HTMLElement>('[role="listbox"]');
  const search = () => document.querySelector<HTMLInputElement>('input[role="combobox"]');
  const optionEls = () => Array.from(document.querySelectorAll<HTMLElement>('[role="option"]'));
  const optionBy = (label: string) => optionEls().find((o) => o.textContent?.includes(label));

  function press(
    fixture: ComponentFixture<FacetedFilter>,
    key: string,
    init: KeyboardEventInit = {},
  ): void {
    search()!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, ...init }));
    fixture.detectChanges();
  }

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((el) => (el.innerHTML = ''));
  });

  it('declares a multi-selectable listbox owned by the search combobox', () => {
    const fixture = createFilter({ multiple: true, values: [] });

    expect(listbox()?.getAttribute('aria-multiselectable')).toBe('true');
    expect(search()?.getAttribute('aria-controls')).toBe(listbox()?.id);
    expect(listbox()?.id).toBeTruthy();
    // 输入会过滤候选集：必须声明 list 型自动补全（autocomplete="off" 只管浏览器表单填充）。
    expect(search()?.getAttribute('aria-autocomplete')).toBe('list');
    // 触发器打开的是 popover（dialog）：先确认元素存在，再精确断言语义与其受控元素一致。
    const trigger = document.querySelector<HTMLElement>('button[hlmpopovertrigger]');
    expect(trigger, 'popover trigger must render').not.toBeNull();
    expect(trigger!.getAttribute('aria-haspopup')).toBe('dialog');
    const controlledId = trigger!.getAttribute('aria-controls');
    expect(controlledId, 'trigger must reference the overlay').toBeTruthy();
    expect(document.getElementById(controlledId!)?.getAttribute('role')).toBe('dialog');

    fixture.componentRef.setInput('multiple', false);
    fixture.detectChanges();
    expect(listbox()?.getAttribute('aria-multiselectable')).toBeNull();
  });

  it('names the overlay dialog after the filter label', () => {
    const fixture = createFilter({ multiple: true, values: [] });

    // Brain 把 role="dialog" 设在 CDK overlay pane 上（不是内容元素），共享指令负责为其命名。
    const pane = document.querySelector('.cdk-overlay-pane[role="dialog"]');
    expect(pane, 'popover should render into a dialog overlay pane').not.toBeNull();
    expect(pane!.getAttribute('aria-label')).toBe('Role');

    fixture.componentRef.setInput('label', 'Status');
    fixture.detectChanges();
    expect(pane!.getAttribute('aria-label'), 'label changes must propagate to the pane').toBe(
      'Status',
    );
  });

  it('reports business selection through aria-selected in both modes', () => {
    const fixture = createFilter({ multiple: true, values: ['admin'] });
    expect(optionBy('Administrator')?.getAttribute('aria-selected')).toBe('true');
    expect(optionBy('Member')?.getAttribute('aria-selected')).toBe('false');

    fixture.componentRef.setInput('multiple', false);
    fixture.componentRef.setInput('value', 'guest');
    fixture.detectChanges();
    expect(optionBy('Guest')?.getAttribute('aria-selected')).toBe('true');
    expect(optionBy('Administrator')?.getAttribute('aria-selected')).toBe('false');
  });

  it('moves the keyboard active option without changing the selection', () => {
    const fixture = createFilter({ multiple: true, values: ['admin'] });
    const selectedBefore = optionEls().map((o) => o.getAttribute('aria-selected'));

    expect(search()?.getAttribute('aria-activedescendant')).toBe(optionEls()[0].id);
    press(fixture, 'ArrowDown');
    expect(search()?.getAttribute('aria-activedescendant')).toBe(optionEls()[1].id);
    press(fixture, 'End');
    expect(search()?.getAttribute('aria-activedescendant')).toBe(optionEls()[2].id);
    press(fixture, 'ArrowDown'); // 环绕回首项
    expect(search()?.getAttribute('aria-activedescendant')).toBe(optionEls()[0].id);
    press(fixture, 'ArrowUp'); // 环绕到末项
    expect(search()?.getAttribute('aria-activedescendant')).toBe(optionEls()[2].id);

    expect(optionEls().map((o) => o.getAttribute('aria-selected'))).toEqual(selectedBefore);
  });

  it('toggles the active option with Enter and keeps the multi-select panel open', () => {
    const fixture = createFilter({ multiple: true, values: ['admin'] });
    let emitted: string[] | null = null;
    fixture.componentInstance.valuesChange.subscribe((values) => (emitted = values));

    press(fixture, 'ArrowDown');
    press(fixture, 'Enter');

    expect(emitted!).toEqual(['admin', 'member']);
    expect(fixture.componentInstance.state()).toBe('open');
  });

  it('ignores keys composed by an IME so confirming a candidate never selects', () => {
    const fixture = createFilter({ multiple: true, values: ['admin'] });
    let emitted: string[] | null = null;
    fixture.componentInstance.valuesChange.subscribe((values) => (emitted = values));

    press(fixture, 'ArrowDown', { isComposing: true });
    expect(search()?.getAttribute('aria-activedescendant')).toBe(optionEls()[0].id);

    press(fixture, 'Enter', { isComposing: true });
    expect(emitted).toBeNull();
    expect(fixture.componentInstance.state()).toBe('open');

    // 合成结束后按键恢复正常。
    press(fixture, 'Enter');
    expect(emitted!).toEqual([]);
  });

  it('keeps the empty state outside the listbox so it only contains options', () => {
    const fixture = createFilter({ multiple: true, values: [] });

    const input = search()!;
    input.value = 'zzz-no-match';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(optionEls().length).toBe(0);
    expect(listbox()?.children.length).toBe(0);
    const empty = Array.from(document.querySelectorAll('.cdk-overlay-container div')).find(
      (el) => el.textContent?.trim() === 'No results',
    );
    expect(empty, 'empty state should still render').toBeDefined();
    expect(listbox()?.contains(empty!)).toBe(false);
  });

  it('emits the single-select value on click and closes the panel', () => {
    const fixture = createFilter({ value: null });
    let emitted: unknown = 'untouched';
    fixture.componentInstance.valueChange.subscribe((value) => (emitted = value));

    optionBy('Guest')?.click();
    fixture.detectChanges();

    expect(emitted).toBe('guest');
    expect(fixture.componentInstance.state()).toBe('closed');
  });

  it('filters options by the search query and resets the active option', () => {
    const fixture = createFilter({ multiple: true, values: [] });

    press(fixture, 'End');
    expect(search()?.getAttribute('aria-activedescendant')).toBe(optionEls()[2].id);

    const input = search()!;
    input.value = 'mem';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(optionEls().length).toBe(1);
    expect(optionEls()[0].textContent).toContain('Member');
    expect(search()?.getAttribute('aria-activedescendant')).toBe(optionEls()[0].id);
  });

  it('keeps the clear command outside the listbox and free of option semantics', () => {
    createFilter({ multiple: true, values: ['admin'] });

    const clear = Array.from(document.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Clear filter'),
    );
    expect(clear, 'clear button should render when a selection exists').toBeDefined();
    expect(clear?.getAttribute('role')).toBeNull();
    expect(listbox()?.contains(clear!)).toBe(false);
  });

  it('renders options without nested interactive elements', () => {
    createFilter({ multiple: true, values: ['admin'] });

    for (const option of optionEls()) {
      expect(
        option.querySelector('button, input, [tabindex], [role="checkbox"]'),
        `option "${option.textContent?.trim()}" must not nest interactive elements`,
      ).toBeNull();
    }
  });

  // 已选项是分类标签：用中性变体，主色留给主操作
  it('shows the selection on the trigger as neutral badges in both modes', () => {
    const badges = (fixture: ComponentFixture<FacetedFilter>) =>
      Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('[data-slot="badge"]'),
      ).map((b) => [b.textContent?.trim(), b.getAttribute('data-variant')]);

    const multi = createFilter({ multiple: true, values: ['admin', 'member', 'guest'] });
    expect(badges(multi)).toEqual([
      ['Administrator', 'secondary'],
      ['Member', 'secondary'],
      ['+1', 'secondary'],
    ]);

    const single = createFilter({ value: 'guest' });
    expect(badges(single)).toEqual([['Guest', 'secondary']]);
  });

  it('uses a distinct checked background in dark mode', () => {
    document.documentElement.classList.add('dark');
    try {
      createFilter({ multiple: true, values: ['admin'] });
      const graphic = (label: string) =>
        optionBy(label)?.querySelector<HTMLElement>('span[aria-hidden]');
      const checked = getComputedStyle(graphic('Administrator')!).backgroundColor;
      const unchecked = getComputedStyle(graphic('Member')!).backgroundColor;
      expect(checked).not.toBe(unchecked);
    } finally {
      document.documentElement.classList.remove('dark');
    }
  });
});
