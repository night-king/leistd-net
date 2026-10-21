import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { TablePaginator, TablePaginatorLabels } from './table-paginator';

/** 分页栏的边界状态与意图回传：按钮禁用完全由 canPrev/canNext 决定，组件不自行推断页码。 */
describe('TablePaginator', () => {
  let fixture: ComponentFixture<TablePaginator>;
  let component: TablePaginator;

  const labels: TablePaginatorLabels = {
    currentPageReport: '第 1 - 10 条，共 42 条',
    rowsPerPage: '每页条数',
    page: '第 1 / 5 页',
    first: '首页',
    previous: '上一页',
    next: '下一页',
    last: '末页',
  };

  function buttonFor(label: string): HTMLButtonElement {
    const found = fixture.debugElement
      .queryAll(By.css('button'))
      .map((item) => item.nativeElement as HTMLButtonElement)
      .find((element) => element.getAttribute('aria-label') === label);

    if (!found) {
      throw new Error(`Button not found: ${label}`);
    }

    return found;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [TablePaginator] }).compileComponents();

    fixture = TestBed.createComponent(TablePaginator);
    component = fixture.componentInstance;

    fixture.componentRef.setInput('labels', labels);
    fixture.componentRef.setInput('rows', 10);
    fixture.componentRef.setInput('canPrev', false);
    fixture.componentRef.setInput('canNext', true);
    fixture.detectChanges();
  });

  it('disables first and previous on the first page but keeps next and last enabled', () => {
    expect(buttonFor('首页').disabled).toBe(true);
    expect(buttonFor('上一页').disabled).toBe(true);
    expect(buttonFor('下一页').disabled).toBe(false);
    expect(buttonFor('末页').disabled).toBe(false);
  });

  it('disables next and last on the last page', () => {
    fixture.componentRef.setInput('canPrev', true);
    fixture.componentRef.setInput('canNext', false);
    fixture.detectChanges();

    expect(buttonFor('首页').disabled).toBe(false);
    expect(buttonFor('上一页').disabled).toBe(false);
    expect(buttonFor('下一页').disabled).toBe(true);
    expect(buttonFor('末页').disabled).toBe(true);
  });

  it('disables all four buttons when there is only one page', () => {
    fixture.componentRef.setInput('canPrev', false);
    fixture.componentRef.setInput('canNext', false);
    fixture.detectChanges();

    for (const label of ['首页', '上一页', '下一页', '末页']) {
      expect(buttonFor(label).disabled, label).toBe(true);
    }
  });

  it('emits the matching intent when an enabled navigation button is clicked', () => {
    fixture.componentRef.setInput('canPrev', true);
    fixture.componentRef.setInput('canNext', true);
    fixture.detectChanges();

    const emitted: string[] = [];
    component.firstPage.subscribe(() => emitted.push('first'));
    component.prevPage.subscribe(() => emitted.push('prev'));
    component.nextPage.subscribe(() => emitted.push('next'));
    component.lastPage.subscribe(() => emitted.push('last'));

    buttonFor('首页').click();
    buttonFor('上一页').click();
    buttonFor('下一页').click();
    buttonFor('末页').click();

    expect(emitted).toEqual(['first', 'prev', 'next', 'last']);
  });

  it('renders the page info passed in by the parent instead of computing its own', () => {
    const report = fixture.nativeElement.textContent as string;

    // 分页状态由父表格持有，本组件只渲染；自己算一份必然与父级的口径分叉。
    expect(report).toContain('第 1 - 10 条，共 42 条');
    expect(report).toContain('第 1 / 5 页');
  });

  // 可读名要落在真正的 combobox 按钮上，宿主元素上的 aria-label 读屏读不到
  it('names the rows-per-page combobox', () => {
    const combobox = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      'button[role="combobox"]',
    )!;

    expect(combobox.getAttribute('aria-label')).toBe('每页条数');
    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('hlm-select-trigger')!
        .hasAttribute('aria-label'),
    ).toBe(false);
  });

  // 避免把 null 当成合法页大小。
  it('does not emit when the rows-per-page value is empty', () => {
    const emitted: (number | null)[] = [];
    component.rowsChange.subscribe((value) => emitted.push(value));

    component.onRowsChange(null);
    component.onRowsChange(undefined);
    expect(emitted).toEqual([]);

    component.onRowsChange(50);
    expect(emitted).toEqual([50]);
  });
});
