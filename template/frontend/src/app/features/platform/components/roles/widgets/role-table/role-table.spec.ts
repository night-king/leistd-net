import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { PaginationState, SortingState } from '@tanstack/angular-table';
import { BehaviorSubject } from 'rxjs';

import { RoleTable } from './role-table';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { expectAriaSortOnSortedHeaderOnly } from '../../../../../../shared/utils/table-sorting.testing';
import { RoleOutputDto } from '../../../../dtos/role.dto';

/** 角色表格与用户表格共用受控分页与排序约定，但各自实现，因此各自用例钉住。 */
describe('RoleTable', () => {
  let fixture: ComponentFixture<RoleTable>;
  let component: RoleTable;
  let viewport: BehaviorSubject<BreakpointState>;

  const desktop = (matches: boolean): BreakpointState => ({
    matches,
    breakpoints: {
      '(min-width: 768px)': matches,
      '(min-width: 1024px)': matches,
    },
  });

  function role(id: string, name: string): RoleOutputDto {
    return {
      id,
      name,
      displayName: name,
      isDefault: false,
      isStatic: false,
      sort: 1,
      userCount: 0,
      permissionCount: 0,
      creationTime: '2026-01-01T00:00:00Z',
    } as unknown as RoleOutputDto;
  }

  beforeEach(async () => {
    viewport = new BehaviorSubject<BreakpointState>(desktop(true));

    await TestBed.configureTestingModule({
      imports: [RoleTable],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en', 'zh-CN']),
        //#endif
        {
          provide: BreakpointObserver,
          useValue: { observe: () => viewport.asObservable() },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RoleTable);
    component = fixture.componentInstance;

    fixture.componentRef.setInput('roles', [role('1', 'admin'), role('2', 'member')]);
    fixture.componentRef.setInput('totalCount', 25);
    fixture.componentRef.setInput('pagination', { pageIndex: 0, pageSize: 10 } as PaginationState);
    fixture.detectChanges();
  });

  it("derives the current page and page count from the parent's paging state", () => {
    expect(component.currentPage()).toBe(1);
    expect(component.totalPages()).toBe(3); // 25 条 / 每页 10

    fixture.componentRef.setInput('pagination', { pageIndex: 1, pageSize: 10 } as PaginationState);
    fixture.detectChanges();

    expect(component.currentPage()).toBe(2);
  });

  it('returns to the first page when rows per page changes', () => {
    fixture.componentRef.setInput('pagination', { pageIndex: 2, pageSize: 10 } as PaginationState);
    fixture.detectChanges();

    const emitted: PaginationState[] = [];
    component.paginationChange.subscribe((value) => emitted.push(value));

    component.changePageSize(100);

    expect(emitted).toEqual([{ pageIndex: 0, pageSize: 100 }]);
  });

  it('toggles between ascending and descending on sort click and emits the sort intent', () => {
    const emitted: SortingState[] = [];
    component.sortingChange.subscribe((value) => emitted.push(value));

    component.toggleSort('displayName');
    expect(emitted[0]).toEqual([{ id: 'displayName', desc: false }]);

    fixture.componentRef.setInput('sorting', emitted[0]);
    fixture.detectChanges();

    component.toggleSort('displayName');
    expect(emitted[1]).toEqual([{ id: 'displayName', desc: true }]);
  });

  it('puts aria-sort only on the header of the sorted column', async () => {
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    // 表头格属于哪一列，以点它的排序按钮发出的排序意图为准
    const sortedColumns: string[] = [];
    fixture.componentInstance.sortingChange.subscribe((value) => sortedColumns.push(value[0].id));
    const columnOf = (header: HTMLTableCellElement | null) => {
      header!.querySelector<HTMLButtonElement>(':scope > button')!.click();
      return sortedColumns.at(-1);
    };
    expectAriaSortOnSortedHeaderOnly(host, null);

    fixture.componentRef.setInput('sorting', [{ id: 'displayName', desc: false }] as SortingState);
    fixture.detectChanges();
    const first = expectAriaSortOnSortedHeaderOnly(host, 'ascending');
    expect(columnOf(first)).toBe('displayName');

    // 换列后旧表头的属性随之消失
    fixture.componentRef.setInput('sorting', [{ id: 'sort', desc: true }] as SortingState);
    fixture.detectChanges();
    const second = expectAriaSortOnSortedHeaderOnly(host, 'descending');
    expect(second).not.toBe(first);
    expect(columnOf(second)).toBe('sort');
  });

  it('does not render the overflow menu when no action is available', () => {
    fixture.componentRef.setInput('canUpdate', false);
    fixture.componentRef.setInput('canDelete', false);
    fixture.componentRef.setInput('canManagePermissions', false);
    fixture.detectChanges();

    expect(component.hasRowActions()).toBe(false);

    fixture.componentRef.setInput('canManagePermissions', true);
    fixture.detectChanges();
    expect(component.hasRowActions()).toBe(true);
  });

  it('flags collapsed columns on a narrow viewport', () => {
    expect(component.hasCollapsedColumns()).toBe(false);

    viewport.next(desktop(false));
    fixture.detectChanges();

    expect(component.hasCollapsedColumns()).toBe(true);
  });

  // 展开状态用 TanStack 的行展开，按行 id 记：数据刷新（同一批行换了新对象、换了顺序）后
  // 展开的仍是原来那一行。按下标记会让展开跟着位置走，数据一换就默认全部收起。
  it('keeps a row expanded by its id across data refreshes', async () => {
    viewport.next(desktop(false));
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    expandButtons()[0].click();
    await fixture.whenStable();
    expect(expandedStates()).toEqual(['true', 'false']);

    fixture.componentRef.setInput('roles', [role('2', 'member'), role('1', 'admin')]);
    await fixture.whenStable();
    expect(expandedStates()).toEqual(['false', 'true']);

    expandButtons()[1].click();
    await fixture.whenStable();
    expect(expandedStates()).toEqual(['false', 'false']);
  });

  function expandButtons(): HTMLButtonElement[] {
    const host = fixture.nativeElement as HTMLElement;
    return Array.from(host.querySelectorAll('tbody ng-icon[name="lucideChevronRight"]')).map(
      (icon) => icon.closest('button')!,
    );
  }

  function expandedStates(): (string | null)[] {
    return expandButtons().map((button) => button.getAttribute('aria-expanded'));
  }
  //#if (IncludeLocalization)

  // 表头与分页文案都经模板结构指令的 t 取得：切换语言后已渲染的表格要换成新语言，
  // OnPush 视图不会因为别的原因被标脏，停在旧语言就说明文案没有走结构指令。
  it('re-renders header and paginator labels when the language changes', async () => {
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    const transloco = TestBed.inject(TranslocoService);
    transloco.setTranslation(
      { common: { rowsPerPage: '每页条数' }, roles: { colName: '角色' } },
      'zh-CN',
    );
    const host = fixture.nativeElement as HTMLElement;
    const headers = () =>
      Array.from(host.querySelectorAll('th')).map((th) => th.textContent!.trim());
    expect(headers()).toContain('roles.colName');
    expect(host.textContent).not.toContain('每页条数');

    transloco.setActiveLang('zh-CN');
    await fixture.whenStable();

    expect(headers()).toContain('角色');
    expect(host.textContent).toContain('每页条数');
  });
  //#endif
  it('shows the active row write on its closed action menu without locking other rows', async () => {
    fixture.componentRef.setInput('canDelete', true);
    fixture.componentRef.setInput('deletingId', '1');
    fixture.detectChanges();
    for (const block of await fixture.getDeferBlocks())
      await block.render(DeferBlockState.Complete);
    await fixture.whenStable();
    const buttons = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>(
        'tbody button[aria-haspopup="menu"]',
      ),
    );
    expect(buttons.length).toBe(2);
    expect(buttons[0].disabled).toBe(true);
    expect(buttons[0].querySelector('hlm-spinner')).not.toBeNull();
    expect(buttons[1].disabled).toBe(false);
    fixture.componentRef.setInput('deletingId', null);
    fixture.detectChanges();
    expect(buttons[0].disabled).toBe(false);
  });
});
