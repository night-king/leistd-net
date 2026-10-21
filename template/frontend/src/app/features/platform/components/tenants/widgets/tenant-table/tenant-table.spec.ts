import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PaginationState } from '@tanstack/angular-table';
import { BehaviorSubject } from 'rxjs';

import { TenantTable } from './tenant-table';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { TenantOutputDto } from '../../../../dtos/tenant.dto';

/**
 * 租户表格与用户、角色表格共用受控分页与列收纳约定，但各自实现，因此各自用例钉住。
 * 租户列表不支持排序。
 */
describe('TenantTable', () => {
  let fixture: ComponentFixture<TenantTable>;
  let component: TenantTable;
  let viewport: BehaviorSubject<BreakpointState>;

  const viewportState = (medium: boolean, large: boolean): BreakpointState => ({
    matches: medium || large,
    breakpoints: {
      '(min-width: 768px)': medium,
      '(min-width: 1024px)': large,
    },
  });

  function tenant(id: string, name: string): TenantOutputDto {
    return {
      id,
      name,
      displayName: name.toUpperCase(),
      isActive: true,
      creationTime: '2026-01-01T00:00:00Z',
    } as unknown as TenantOutputDto;
  }

  beforeEach(async () => {
    viewport = new BehaviorSubject<BreakpointState>(viewportState(true, true));

    await TestBed.configureTestingModule({
      imports: [TenantTable],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        {
          provide: BreakpointObserver,
          useValue: { observe: () => viewport.asObservable() },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(TenantTable);
    component = fixture.componentInstance;

    fixture.componentRef.setInput('tenants', [tenant('1', 'acme'), tenant('2', 'globex')]);
    fixture.componentRef.setInput('totalCount', 45);
    fixture.componentRef.setInput('pagination', { pageIndex: 0, pageSize: 20 } as PaginationState);
    fixture.detectChanges();
  });

  it('derives the current page and total pages from the parent pagination state', () => {
    expect(component.currentPage()).toBe(1);
    expect(component.totalPages()).toBe(3); // 45 条 / 每页 20

    fixture.componentRef.setInput('pagination', { pageIndex: 2, pageSize: 20 } as PaginationState);
    fixture.detectChanges();

    expect(component.currentPage()).toBe(3);
  });

  it('keeps total pages at 1 when there is no data', () => {
    fixture.componentRef.setInput('tenants', []);
    fixture.componentRef.setInput('totalCount', 0);
    fixture.detectChanges();

    // 分页栏写着「第 1 / 0 页」，看上去像是数据加载失败了。
    expect(component.totalPages()).toBe(1);
  });

  it('returns to the first page when the page size changes', () => {
    fixture.componentRef.setInput('pagination', { pageIndex: 2, pageSize: 20 } as PaginationState);
    fixture.detectChanges();

    const emitted: PaginationState[] = [];
    component.paginationChange.subscribe((value) => emitted.push(value));

    component.changePageSize(50);

    // 停在第 3 页却换成每页 50 条，多半越界，用户看到的是一片空白。
    expect(emitted).toEqual([{ pageIndex: 0, pageSize: 50 }]);
  });

  // 列里只写到分钟；完整时刻（含秒）放进可聚焦的提示，键盘用户也拿得到
  it('puts the full creation time into a focusable tooltip on the short time cell', async () => {
    const [tableBlock] = await fixture.getDeferBlocks();
    await tableBlock.render(DeferBlockState.Complete);
    await fixture.whenStable();
    onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());

    const cell = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      'tbody tr span[tabindex="0"].whitespace-nowrap',
    );
    expect(cell).not.toBeNull();
    expect(cell!.textContent).not.toMatch(/\d:\d{2}:\d{2}/);

    cell!.focus();

    await expect
      .poll(() => document.querySelector('[role="tooltip"]')?.textContent ?? '')
      .toMatch(/\d:\d{2}:\d{2}/);
  });

  // "详情"是只读项，菜单入口不随修改权限消失，否则只有查看权限的人没有详情入口。
  it('still shows the row action menu trigger with view-only permission', async () => {
    fixture.componentRef.setInput('canUpdate', false);
    fixture.componentRef.setInput('canDelete', false);

    // 表格整体包在 @defer 里（默认 on idle），测试里得显式把它渲染出来，
    // 否则 DOM 查询永远是空的——那会让断言"通过"成假的。
    const [tableBlock] = await fixture.getDeferBlocks();
    await tableBlock.render(DeferBlockState.Complete);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    const rows = host.querySelectorAll('tbody tr');
    // 用图标定位而不是 aria-label：后者随语言变，图标是结构
    const triggers = host.querySelectorAll('tbody ng-icon[name="lucideEllipsis"]');

    // 按 canUpdate||canDelete 裁剪时这里会是 0，只有查看权限的人也就没有了详情入口。
    expect(rows.length).toBe(2);
    expect(triggers.length).toBe(2);
  });

  //#if (Impersonation)
  // 后端对停用租户直接拒绝模拟登录，留一个点得动的入口就是「能看见但调不通」。
  // 这条钉的是禁用本身：改成隐藏或恢复可点都会红。
  it('keeps the impersonate item for inactive tenants but disables it', async () => {
    const inactive = { ...tenant('2', 'globex'), isActive: false } as TenantOutputDto;
    fixture.componentRef.setInput('tenants', [tenant('1', 'acme'), inactive]);
    fixture.componentRef.setInput('canImpersonate', true);
    fixture.detectChanges();

    const [tableBlock] = await fixture.getDeferBlocks();
    await tableBlock.render(DeferBlockState.Complete);
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    const triggers = host.querySelectorAll<HTMLElement>('tbody ng-icon[name="lucideEllipsis"]');

    async function impersonateItem(index: number): Promise<HTMLButtonElement> {
      triggers[index].closest('button')!.click();
      fixture.detectChanges();
      await fixture.whenStable();
      // 菜单挂在 document 上的浮层里，不在组件宿主内
      return document
        .querySelector('[data-slot="dropdown-menu"] ng-icon[name="lucideLogIn"]')!
        .closest('button') as HTMLButtonElement;
    }

    // 关菜单用 Esc：删掉浮层容器会让 CDK 拿着已脱离文档的引用，之后再也打不开
    async function closeMenu(): Promise<void> {
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      fixture.detectChanges();
      await fixture.whenStable();
    }

    expect((await impersonateItem(1)).disabled).toBe(true);
    await closeMenu();

    expect((await impersonateItem(0)).disabled).toBe(false);
    await closeMenu();
  });

  //#else
  it('keeps tenant management actions without an impersonation entry', async () => {
    const [tableBlock] = await fixture.getDeferBlocks();
    await tableBlock.render(DeferBlockState.Complete);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const trigger = host.querySelector<HTMLElement>('tbody ng-icon[name="lucideEllipsis"]');
    trigger!.closest('button')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const menu = document.querySelector('[data-slot="dropdown-menu"]');
    expect(menu?.querySelector('ng-icon[name="lucideLogIn"]')).toBeNull();
    const edit = menu?.querySelector('ng-icon[name="lucidePencil"]');
    expect(edit).not.toBeNull();
    expect(menu?.querySelector('ng-icon[name="lucideTrash2"]')).not.toBeNull();
    const updated: TenantOutputDto[] = [];
    component.edit.subscribe((value) => updated.push(value));
    edit!.closest('button')!.click();
    expect(updated).toEqual([tenant('1', 'acme')]);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    await fixture.whenStable();
  });
  //#endif
  it('flags collapsed columns on narrow viewports but not on desktop', () => {
    expect(component.hasCollapsedColumns()).toBe(false);

    viewport.next(viewportState(false, false));
    fixture.detectChanges();

    // 列被藏起来却不给展开入口，那些字段就等于从界面上消失了。
    expect(component.hasCollapsedColumns()).toBe(true);
  });

  // 展开详情按列定义顺序列出被收起的数据列：平板先收三级列（描述、创建时间），手机再收二级列。
  // 名称是租户的业务标识、操作列是唯一入口，两者在各档都留在主行且可用——详情里不出现它们
  // 证明不了这一点（它们本来就不登记详情），所以逐档在主行里核对
  it('keeps the name and actions columns in the row while collapsing data columns in order', async () => {
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();
    expectNameAndActionsInRows();

    viewport.next(viewportState(true, false));
    await fixture.whenStable();
    expectNameAndActionsInRows();
    expandButtons()[0].click();
    await fixture.whenStable();
    expect(detailColumns()).toEqual(['description', 'creationTime']);

    viewport.next(viewportState(false, false));
    await fixture.whenStable();
    expectNameAndActionsInRows();
    expect(detailColumns()).toEqual(['displayName', 'description', 'status', 'creationTime']);
  });

  // 展开状态用 TanStack 的行展开，按行 id 记：数据刷新（同一批行换了新对象、换了顺序）后
  // 展开的仍是原来那一行。按下标记会让展开跟着位置走，数据一换就默认全部收起。
  it('keeps a row expanded by its id across data refreshes', async () => {
    viewport.next(viewportState(false, false));
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    expandButtons()[0].click();
    await fixture.whenStable();
    expect(expandedStates()).toEqual(['true', 'false']);

    fixture.componentRef.setInput('tenants', [tenant('2', 'globex'), tenant('1', 'acme')]);
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

  /** 每个数据行的首格是租户名称，末格是可见可点的操作菜单入口；表头的首末格与之对应。 */
  function expectNameAndActionsInRows(): void {
    const host = fixture.nativeElement as HTMLElement;
    const headers = Array.from(host.querySelectorAll('thead th'));
    expect(headers[0].textContent).toMatch(/Name|tenants\.colName/);
    expect(headers.at(-1)!.textContent).toMatch(/Actions|common\.actions/);
    const rows = Array.from(host.querySelectorAll('tbody tr')).filter((row) =>
      row.querySelector('ng-icon[name="lucideChevronRight"], ng-icon[name="lucideEllipsis"]'),
    );
    expect(rows.map((row) => row.querySelector('td')!.textContent!.trim())).toEqual([
      'acme',
      'globex',
    ]);
    for (const row of rows) {
      const trigger = row
        .querySelector('td:last-child ng-icon[name="lucideEllipsis"]')
        ?.closest('button');
      expect(trigger, 'row action menu trigger').toBeTruthy();
      expect(trigger!.getBoundingClientRect().width).toBeGreaterThan(0);
      expect(trigger!.disabled).toBe(false);
    }
  }

  function detailColumns(): (string | null)[] {
    const host = fixture.nativeElement as HTMLElement;
    return Array.from(host.querySelectorAll('[data-detail-column]')).map((dd) =>
      dd.getAttribute('data-detail-column'),
    );
  }

  function expandedStates(): (string | null)[] {
    return expandButtons().map((button) => button.getAttribute('aria-expanded'));
  }
});
