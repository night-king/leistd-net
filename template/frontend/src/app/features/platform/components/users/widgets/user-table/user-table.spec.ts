import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { PaginationState, RowSelectionState, SortingState } from '@tanstack/angular-table';
import { BehaviorSubject } from 'rxjs';
import { page, userEvent } from 'vitest/browser';

import { UserTable } from './user-table';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../../../core/services/auth-service';
import {
  expectTouchTarget,
  setCoarsePointer,
  tapAt,
} from '../../../../../../shared/utils/coarse-pointer.testing';
import { expectAriaSortOnSortedHeaderOnly } from '../../../../../../shared/utils/table-sorting.testing';
import { UserManagementOutputDto } from '../../../../dtos/user-management.dto';

/** 用户表格的交互状态：分页与排序是 manual 模式，验证回传给父级的意图，而不是表格里剩几行。 */
describe('UserTable', () => {
  let fixture: ComponentFixture<UserTable>;
  let component: UserTable;
  let viewport: BehaviorSubject<BreakpointState>;
  const currentUser = signal<{ id: string } | null>(null);

  const desktop = (matches: boolean): BreakpointState => ({
    matches,
    breakpoints: {
      '(min-width: 768px)': matches,
      '(min-width: 1024px)': matches,
    },
  });

  /** 内置超管且开了两步验证：菜单里各项（含重置两步验证）都会出现。 */
  const superAdmin = { isSuperAdmin: true, isTwoFactorEnabled: true };
  const SUPER_ADMIN_REASON = /to their own account|superAdminNoManage/;

  function user(id: string, username: string): UserManagementOutputDto {
    return {
      id,
      username,
      email: `${username}@example.test`,
      displayName: username,
      isActive: true,
      emailConfirmed: true,
      creationTime: '2026-01-01T00:00:00Z',
      roles: [],
    } as unknown as UserManagementOutputDto;
  }

  beforeEach(async () => {
    viewport = new BehaviorSubject<BreakpointState>(desktop(true));
    currentUser.set(null);

    await TestBed.configureTestingModule({
      imports: [UserTable],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { currentUser } },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        {
          provide: BreakpointObserver,
          useValue: { observe: () => viewport.asObservable() },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(UserTable);
    component = fixture.componentInstance;

    fixture.componentRef.setInput('users', [user('1', 'alice'), user('2', 'bob')]);
    fixture.componentRef.setInput('totalCount', 42);
    fixture.componentRef.setInput('pagination', { pageIndex: 0, pageSize: 20 } as PaginationState);
    fixture.detectChanges();
  });

  //#if (LocalIdentity)
  it('shows a locked badge only on locked-out users', async () => {
    fixture.componentRef.setInput('users', [
      { ...user('1', 'alice'), isLockedOut: true, lockoutEnd: '2026-01-01T00:15:00Z' },
      user('2', 'bob'),
    ]);
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const badges = host.querySelectorAll('[data-testid="user-locked"]');
    expect(badges.length).toBe(1);
    expect(badges[0].closest('tr')?.textContent).toContain('alice');
  });

  //#endif
  it('derives the current page and total pages from the parent pagination state', () => {
    expect(component.currentPage()).toBe(1);
    expect(component.totalPages()).toBe(3); // 42 条 / 每页 20

    fixture.componentRef.setInput('pagination', { pageIndex: 2, pageSize: 20 } as PaginationState);
    fixture.detectChanges();

    expect(component.currentPage()).toBe(3);
  });

  // 不出现"第 1 / 0 页"
  it('keeps total pages at 1 when there is no data', () => {
    fixture.componentRef.setInput('users', []);
    fixture.componentRef.setInput('totalCount', 0);
    fixture.detectChanges();

    expect(component.totalPages()).toBe(1);
  });

  it('returns to the first page when the page size changes', () => {
    fixture.componentRef.setInput('pagination', { pageIndex: 3, pageSize: 20 } as PaginationState);
    fixture.detectChanges();

    const emitted: PaginationState[] = [];
    component.paginationChange.subscribe((value) => emitted.push(value));

    component.changePageSize(50);

    // 停在第 4 页却换成每页 50 条，多半越界，用户看到的是一片空白。
    expect(emitted).toEqual([{ pageIndex: 0, pageSize: 50 }]);
  });

  it('toggles between ascending and descending on sort and emits the sorting intent', () => {
    const emitted: SortingState[] = [];
    component.sortingChange.subscribe((value) => emitted.push(value));

    component.toggleSort('username');
    expect(emitted[0]).toEqual([{ id: 'username', desc: false }]);

    // 表格是受控的：把上一轮的排序回灌进去，才能模拟父级更新后的下一次点击。
    fixture.componentRef.setInput('sorting', emitted[0]);
    fixture.detectChanges();

    component.toggleSort('username');
    expect(emitted[1]).toEqual([{ id: 'username', desc: true }]);
  });

  it('updates the sort icon and aria state with the current sorting', () => {
    expect(component.sortIcon('username')).toBe('lucideArrowUpDown');
    expect(component.sortAria('username')).toBeNull();

    fixture.componentRef.setInput('sorting', [{ id: 'username', desc: false }] as SortingState);
    fixture.detectChanges();
    expect(component.sortIcon('username')).toBe('lucideSortAsc');
    expect(component.sortAria('username')).toBe('ascending');

    fixture.componentRef.setInput('sorting', [{ id: 'username', desc: true }] as SortingState);
    fixture.detectChanges();
    expect(component.sortIcon('username')).toBe('lucideSortDesc');
    expect(component.sortAria('username')).toBe('descending');
  });

  it('puts aria-sort only on the header of the sorted column', async () => {
    // 邮箱是三级列，容器放得下全部列时才显示
    await page.viewport(1600, 900);
    onTestFinished(() => page.viewport(1280, 800));
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

    fixture.componentRef.setInput('sorting', [{ id: 'username', desc: false }] as SortingState);
    fixture.detectChanges();
    const first = expectAriaSortOnSortedHeaderOnly(host, 'ascending');
    expect(columnOf(first)).toBe('username');

    // 换列后旧表头的属性随之消失
    fixture.componentRef.setInput('sorting', [{ id: 'email', desc: true }] as SortingState);
    fixture.detectChanges();
    const second = expectAriaSortOnSortedHeaderOnly(host, 'descending');
    expect(second).not.toBe(first);
    expect(columnOf(second)).toBe('email');
  });

  it('hides the overflow menu when no row action is available', () => {
    fixture.componentRef.setInput('canUpdate', false);
    fixture.componentRef.setInput('canDelete', false);
    fixture.componentRef.setInput('canManageRoles', false);
    fixture.detectChanges();

    // 点开即空的按钮比没有按钮更糟：它承诺了一个并不存在的能力。
    expect(component.hasRowActions()).toBe(false);

    fixture.componentRef.setInput('canDelete', true);
    fixture.detectChanges();
    expect(component.hasRowActions()).toBe(true);
  });

  // 停用是本服务对人员的本地处置，所有形态都随 Users.Update 提供
  it('offers enable or disable in the row menu only with the update permission', async () => {
    fixture.componentRef.setInput('users', [user('1', 'alice')]);
    fixture.componentRef.setInput('canDelete', false);
    fixture.componentRef.setInput('canManageRoles', false);
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());
    const menu = await openRowMenu();
    // 图标名是属性绑定，不反射成 attribute，按文案（或未装词条时的键名）找
    expect(menu.textContent).toMatch(/Disable|users\.tooltip\.disable/);
    //#if (!LocalIdentity)
    // 资料由签发方维护，本服务不提供编辑入口
    expect(menu.querySelector('ng-icon[name="lucidePencil"]')).toBeNull();
    expect(host().querySelector('ng-icon[name="lucidePencil"]')).toBeNull();
    //#else
    expect(menu.querySelector('ng-icon[name="lucidePencil"]')).not.toBeNull();
    //#endif

    fixture.componentRef.setInput('canUpdate', false);
    fixture.detectChanges();
    expect(rowMenuTrigger()).toBeNull();
  });

  // 停用会当场作废本人会话，服务端同样拒绝：本人行置灰，原因就地写在菜单里（禁用项上的提示悬停不到）
  it('greys out disabling and deleting the signed-in user and says why', async () => {
    const menu = await openMenuAs('1');

    const disable = menuButton(menu, /Disable|users\.tooltip\.disable/);
    expect(disable.disabled).toBe(true);
    const disableReason = menu.querySelector('[data-testid="user-self-disable-reason"]')!;
    expect(disable.getAttribute('aria-describedby')).toBe(disableReason.id);
    expect(disableReason.textContent).toMatch(/disable your own account|selfNoDisable/);

    const remove = menuButton(menu, /Delete|common\.delete/);
    expect(remove.disabled).toBe(true);
    const deleteReason = menu.querySelector('[data-testid="user-self-delete-reason"]')!;
    expect(remove.getAttribute('aria-describedby')).toBe(deleteReason.id);
    expect(deleteReason.textContent).toMatch(/delete your own account|selfNoDelete/);
  });

  //#if (LocalIdentity)
  // 管理入口重置自己的两步验证会绕过个人设置要的口令与验证码和组织的强制策略，服务端同样拒绝：本人行置灰并就地说明
  it("greys out resetting the signed-in user's own two-factor and says why", async () => {
    const menu = await openMenuAs('1', { ...user('1', 'alice'), isTwoFactorEnabled: true });

    const reset = menu.querySelector<HTMLButtonElement>('[data-testid="user-reset-two-factor"]')!;
    expect(reset.disabled).toBe(true);
    const reason = menu.querySelector('[data-testid="user-self-reset-two-factor-reason"]')!;
    expect(reset.getAttribute('aria-describedby')).toBe(reason.id);
    expect(reason.textContent).toMatch(/Personal settings|selfNoResetTwoFactor/);
  });

  it("leaves resetting someone else's two-factor available", async () => {
    const menu = await openMenuAs('2', { ...user('1', 'alice'), isTwoFactorEnabled: true });

    expect(
      menu.querySelector<HTMLButtonElement>('[data-testid="user-reset-two-factor"]')!.disabled,
    ).toBe(false);
    expect(menu.querySelector('[data-testid="user-self-reset-two-factor-reason"]')).toBeNull();
  });

  //#endif
  it("leaves disabling and deleting someone else's account available", async () => {
    const menu = await openMenuAs('2');

    expect(menuButton(menu, /Disable|users\.tooltip\.disable/).disabled).toBe(false);
    expect(menuButton(menu, /Delete|common\.delete/).disabled).toBe(false);
    expect(menu.querySelector('[data-testid^="user-self-"]')).toBeNull();
    expect(menu.querySelector('[data-testid^="user-super-admin-"]')).toBeNull();
    expect(host().querySelector('[data-testid^="user-super-admin-"]')).toBeNull();
  });

  // 内置超管的受保护操作只能由本人进行：他人查看其行时这些项置灰，原因在菜单首说一次（禁用项上的提示悬停不到）
  it('greys out the protected actions on the built-in super admin row for other admins and says why', async () => {
    await page.viewport(1280, 800);
    const menu = await openMenuAs('2', { ...user('1', 'admin'), ...superAdmin }, true);

    const reason = menu.querySelector('[data-testid="user-super-admin-reason"]')!;
    expect(reason.textContent).toMatch(SUPER_ADMIN_REASON);
    const disabled = Array.from(menu.querySelectorAll<HTMLButtonElement>('button:disabled'));
    //#if (LocalIdentity)
    // 编辑、分配角色、重置密码、重置两步验证、停用、删除
    expect(disabled.length).toBe(6);
    //#else
    // 分配角色、停用、删除
    expect(disabled.length).toBe(3);
    //#endif
    for (const button of disabled) {
      expect(button.getAttribute('aria-describedby')).toBe(reason.id);
    }

    // 桌面端行内按钮：读屏经 aria-describedby 读到原因，悬停外层可见同一句提示
    const inlineReason = host().querySelector('[data-testid="user-super-admin-inline-reason"]')!;
    expect(inlineReason.textContent).toMatch(SUPER_ADMIN_REASON);
    const inline = inlineButtons();
    //#if (LocalIdentity)
    expect(inline.length).toBe(2);
    //#else
    expect(inline.length).toBe(1);
    //#endif
    for (const button of inline) {
      expect(button.disabled).toBe(true);
      expect(button.getAttribute('aria-describedby')).toBe(inlineReason.id);
      expect(button.parentElement!.getAttribute('tabindex')).toBe('0');
    }
    // 外层可聚焦：键盘聚焦即弹出原因提示（鼠标悬停走同一提示），并由提示自身补 aria-describedby
    const wrapper = inline[0].parentElement!;
    wrapper.focus();
    await expect
      .poll(() => document.querySelector('[role="tooltip"]')?.textContent ?? '')
      .toMatch(SUPER_ADMIN_REASON);
    expect(wrapper.getAttribute('aria-describedby')).toBe(
      document.querySelector('[role="tooltip"]')!.id,
    );
  });

  //#if (LocalIdentity)
  // 解除锁定不在受保护操作之列：锁定的超管仍可由有权限的管理员解锁，原因说明不覆盖它
  it('leaves unlocking a locked super admin available without the protection reason', async () => {
    const menu = await openMenuAs('2', { ...user('1', 'admin'), ...superAdmin, isLockedOut: true });

    const unlock = menu.querySelector<HTMLButtonElement>('[data-testid="user-unlock"]')!;
    expect(unlock.disabled).toBe(false);
    expect(unlock.getAttribute('aria-describedby')).toBeNull();
    expect(menu.querySelector('[data-testid="user-super-admin-reason"]')).not.toBeNull();
  });

  //#endif
  it('keeps the self reasons when the super admin views their own row', async () => {
    const menu = await openMenuAs('1', { ...user('1', 'admin'), ...superAdmin }, true);

    const disable = menuButton(menu, /Disable|users\.tooltip\.disable/);
    expect(disable.disabled).toBe(true);
    expect(disable.getAttribute('aria-describedby')).toBe(
      menu.querySelector('[data-testid="user-self-disable-reason"]')!.id,
    );
    const remove = menuButton(menu, /Delete|common\.delete/);
    expect(remove.disabled).toBe(true);
    expect(remove.getAttribute('aria-describedby')).toBe(
      menu.querySelector('[data-testid="user-self-delete-reason"]')!.id,
    );
    expect(menu.querySelector('[data-testid^="user-super-admin-"]')).toBeNull();
    expect(host().querySelector('[data-testid^="user-super-admin-"]')).toBeNull();
    for (const button of inlineButtons()) {
      expect(button.disabled).toBe(false);
      expect(button.parentElement!.hasAttribute('tabindex')).toBe(false);
    }
  });

  it('hides the menu trigger on desktop when it would only repeat the inline buttons', async () => {
    await page.viewport(1280, 800);
    fixture.componentRef.setInput('users', [user('1', 'alice')]);
    fixture.componentRef.setInput('canUpdate', false);
    fixture.componentRef.setInput('canDelete', false);
    fixture.componentRef.setInput('canManageRoles', true);
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    // 分配角色在桌面端是行内按钮，菜单里只有它的窄屏副本：桌面端点开会是空菜单
    expect(getComputedStyle(rowMenuTrigger()!).display).toBe('none');

    fixture.componentRef.setInput('canUpdate', true);
    fixture.detectChanges();
    expect(getComputedStyle(rowMenuTrigger()!).display).not.toBe('none');
  });

  it('flags collapsed columns on narrow viewports but not on desktop', () => {
    expect(component.hasCollapsedColumns()).toBe(false);

    viewport.next(desktop(false));
    fixture.detectChanges();

    // 列被藏起来却不给展开入口，那些字段就等于从界面上消失了。
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

    fixture.componentRef.setInput('users', [user('2', 'bob'), user('1', 'alice')]);
    await fixture.whenStable();
    expect(expandedStates()).toEqual(['false', 'true']);

    expandButtons()[1].click();
    await fixture.whenStable();
    expect(expandedStates()).toEqual(['false', 'false']);
  });

  describe('on coarse pointers', () => {
    afterEach(async () => {
      await setCoarsePointer(false);
    });

    /** 选择列的复选框与右邻排序按钮、展开按钮各自是 44px 目标，命中区互不覆盖。 */
    async function renderSelectable() {
      await setCoarsePointer(true);
      fixture.componentRef.setInput('selectable', true);
      const [table] = await fixture.getDeferBlocks();
      await table.render(DeferBlockState.Complete);
      await fixture.whenStable();
      // hlmBtn 的尺寸类在插入后才套上，transition-all 会把按钮从细指针尺寸过渡到 44px，量之前等它结束
      await Promise.all(document.getAnimations().map((animation) => animation.finished));
      const selections: RowSelectionState[] = [];
      const sorts: SortingState[] = [];
      component.rowSelectionChange.subscribe((value) => selections.push(value));
      component.sortingChange.subscribe((value) => sorts.push(value));
      const [selectAll, firstRow] = host().querySelectorAll('[role="checkbox"]');
      const sortButton = host().querySelector('thead button:not([role])')!;
      return { selectAll, firstRow, sortButton, selections, sorts };
    }

    // 复选框视觉方框之外、44px 命中区之内
    const EDGE = 20;

    it('gives the select checkboxes and the neighbouring sort button full touch targets', async () => {
      const { selectAll, firstRow, sortButton } = await renderSelectable();

      expectTouchTarget(selectAll);
      expectTouchTarget(firstRow);
      expectTouchTarget(sortButton);
    });

    it('routes taps at the shared edge to the control under them', async () => {
      const { selectAll, firstRow, sortButton, selections, sorts } = await renderSelectable();

      await tapAt(selectAll, EDGE, 0);
      expect(selections).toEqual([{ '1': true, '2': true }]);
      expect(sorts).toEqual([]);

      const sortLeft = sortButton.getBoundingClientRect().width / 2 - 1;
      await tapAt(sortButton, -sortLeft, 0);
      expect(sorts).toEqual([[{ id: 'username', desc: false }]]);
      expect(selections).toHaveLength(1);

      await tapAt(firstRow, EDGE, EDGE);
      expect(selections.at(-1)).toEqual({ '1': true });
    });

    it('keeps the select checkbox and the expand button apart on narrow tables', async () => {
      viewport.next(desktop(false));
      const { firstRow, selections } = await renderSelectable();
      const [expand] = expandButtons();

      expectTouchTarget(firstRow);
      expectTouchTarget(expand);
      await tapAt(firstRow, EDGE, 0);
      expect(selections).toEqual([{ '1': true }]);
      expect(expandedStates()).toEqual(['false', 'false']);
    });
  });

  //#if (LocalIdentity)
  // 窄容器里状态列不放"邮箱未验证"徽章，展开详情补上；只读权限也能看到
  it('shows an unverified email in the expanded row of a narrow table', async () => {
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '480px';
    viewport.next(desktop(false));
    fixture.componentRef.setInput('users', [{ ...user('1', 'alice'), isEmailVerified: false }]);
    fixture.componentRef.setInput('canUpdate', false);
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    expandButtons()[0].click();
    await fixture.whenStable();
    fixture.detectChanges();

    const status = host.querySelector<HTMLElement>('[data-testid="user-detail-email-status"]');
    expect(status).not.toBeNull();
    expect(getComputedStyle(status!).display).not.toBe('none');
  });

  //#endif
  // 时间列写到分钟，完整时刻（含秒）放在可聚焦的提示里：键盘用户也能查到
  it('keeps the full time of a visible time column reachable by keyboard', async () => {
    await page.viewport(1600, 900);
    onTestFinished(() => page.viewport(1280, 800));
    fixture.componentRef.setInput('users', [
      { ...user('1', 'alice'), creationTime: '2026-01-01T08:09:07Z' },
    ]);
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.autoDetectChanges();

    const time = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(
        'tbody span[tabindex="0"]',
      ),
    ).find((span) => /:09/.test(span.textContent ?? ''))!;
    expect(time.textContent).not.toMatch(/:07/);

    time.focus();
    await expect.poll(() => document.body.textContent?.includes(':09:07')).toBe(true);
  });

  // 最窄一档无列可收时主列截断，表格不横向滚动，状态列与操作列完整可见。
  it('truncates a long name instead of scrolling the table at the narrowest level', async () => {
    // 手机视口：`sm:` 断点（单元格内边距、行内操作按钮）也要按手机生效，只模拟档位不够
    await page.viewport(390, 844);
    onTestFinished(() => page.viewport(1280, 800));
    //#if (IncludeLocalization)
    // 测试不装词条时表头渲染成键名，比真实文案宽得多；量宽度要用真实长度的文案
    TestBed.inject(TranslocoService).setTranslation(
      { table: { colUser: 'User', colStatus: 'Status' }, status: { active: 'Active' } },
      'users/en',
    );
    //#endif
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '356px';
    viewport.next(desktop(false));
    fixture.componentRef.setInput('users', [
      { ...user('1', 'admin'), displayName: 'System Administrator of the Whole Organization' },
    ]);
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.autoDetectChanges();

    const scroller = host.querySelector<HTMLElement>('[hlmTableContainer]')!;
    await expect
      .poll(() => host.querySelector('[data-table-fit]')?.getAttribute('data-table-fit'))
      .toBe('mobile');
    await expect.poll(() => scroller.scrollWidth <= scroller.clientWidth).toBe(true);

    const name = host.querySelector<HTMLElement>('tbody span.truncate')!;
    expect(name.scrollWidth).toBeGreaterThan(name.clientWidth);
    const status = host.querySelector<HTMLElement>('tbody [hlmBadge]')!;
    const actions = host.querySelector<HTMLElement>('tbody td:last-child')!;
    expect(status.getBoundingClientRect().right).toBeLessThanOrEqual(
      actions.getBoundingClientRect().left,
    );
  });

  // 最窄一档主列截断了姓名，展开详情给出全文；更宽的档位主列完整，详情不重复
  it('shows the full name in the expanded row only at the narrowest level', async () => {
    await page.viewport(390, 844);
    onTestFinished(() => page.viewport(1280, 800));
    const longName = 'System Administrator of the Whole Organization';
    host().style.display = 'block';
    host().style.width = '356px';
    viewport.next(desktop(false));
    fixture.componentRef.setInput('users', [{ ...user('1', 'admin'), displayName: longName }]);
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.autoDetectChanges();
    await expect
      .poll(() => host().querySelector('[data-table-fit]')?.getAttribute('data-table-fit'))
      .toBe('mobile');

    expandButtons()[0].click();
    await fixture.whenStable();

    const identity = host().querySelector<HTMLElement>('[data-testid="user-detail-identity"]')!;
    expect(getComputedStyle(identity).display).not.toBe('none');
    expect(identity.textContent).toContain(longName);
    expect(identity.textContent).toContain('admin');

    // 视口档位是表格档位的上限，加宽容器之外还要把视口放到桌面
    await page.viewport(1800, 900);
    viewport.next(desktop(true));
    host().style.width = '1600px';
    await expect
      .poll(() => host().querySelector('[data-table-fit]')?.getAttribute('data-table-fit'))
      .not.toBe('mobile');
    // 没有收起的列时展开行整个不渲染；渲染时这一项也不显示
    const wide = host().querySelector<HTMLElement>('[data-testid="user-detail-identity"]');
    expect(wide === null || getComputedStyle(wide).display === 'none').toBe(true);
  });

  describe('roles column', () => {
    const LONG_ROLES = [
      'Regional Compliance Reviewer',
      'Cross-Border Settlement Approver',
      'Customer Onboarding Coordinator',
      'Treasury Operations Supervisor',
      'Internal Audit Liaison Officer',
      'Product Catalogue Maintainer',
      'Partner Programme Administrator',
      'Data Protection Request Handler',
      'Field Service Dispatch Planner',
      'Procurement Contract Reviewer',
      'Quality Assurance Sign-off Lead',
      'Incident Response On-call Engineer',
      'Billing Dispute Resolution Agent',
    ];

    function withRoles(id: string, username: string, names: string[]): UserManagementOutputDto {
      return {
        ...user(id, username),
        roles: names.map((displayName, index) => ({
          id: `${id}-${index}`,
          name: `role-${index}`,
          displayName,
        })),
      };
    }

    async function renderRoles(users: UserManagementOutputDto[]): Promise<void> {
      //#if (IncludeLocalization)
      TestBed.inject(TranslocoService).setTranslation(
        {
          popover: {
            rolesTitle: 'Roles',
            rolesTrigger: 'View all roles of {{name}} ({{count}})',
            filterRoles: 'Filter roles',
            noMatchingRoles: 'No matching roles',
          },
        },
        'users/en',
        { merge: true },
      );
      //#endif
      fixture.componentRef.setInput('users', users);
      const [table] = await fixture.getDeferBlocks();
      await table.render(DeferBlockState.Complete);
      fixture.autoDetectChanges();
      await fixture.whenStable();
      onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());
    }

    function rolesTrigger(row: number): HTMLButtonElement {
      return host().querySelectorAll<HTMLButtonElement>('tbody [data-readonly-list-trigger]')[row];
    }

    async function openRoles(row: number): Promise<HTMLElement> {
      await userEvent.click(rolesTrigger(row));
      await expect
        .poll(() => document.querySelector('[data-testid="readonly-list-panel"]'))
        .not.toBeNull();
      return document.querySelector<HTMLElement>('[data-testid="readonly-list-panel"]')!;
    }

    function panelLabels(panel: HTMLElement): string[] {
      return Array.from(
        panel.querySelectorAll('[data-testid="readonly-list"] li > span + span'),
      ).map((span) => span.firstElementChild!.textContent!.trim());
    }

    // 旧写法把徽章与 "+N" 一起塞进定宽、裁切溢出的容器：英文下 "+N" 被挤出视野，中文下第一个徽章从左侧被切掉。
    // 每个徽章都要整个留在单元格里，且没被祖先裁掉
    it('keeps every role badge and the remaining count visible inside the cell', async () => {
      await renderRoles([withRoles('1', 'alice', LONG_ROLES)]);

      const more = host().querySelector<HTMLElement>('[data-testid="user-roles-more"]')!;
      expect(more.textContent!.trim()).toBe('+11');
      more.scrollIntoView({ block: 'center', inline: 'center' });
      const cell = more.closest('td')!.getBoundingClientRect();
      const badges = Array.from(rolesTrigger(0).querySelectorAll<HTMLElement>('[hlmBadge]'));
      expect(badges).toHaveLength(3);
      for (const badge of badges) {
        const box = badge.getBoundingClientRect();
        const label = badge.textContent!.trim();
        expect(box.width, label).toBeGreaterThan(0);
        expect(box.left, label).toBeGreaterThanOrEqual(cell.left);
        expect(box.right, label).toBeLessThanOrEqual(cell.right);
        // 盒子在单元格里还不够：被祖先裁掉的部分点不到，中心与左右两侧内缩处都要命中徽章自己
        // （不取四角：徽章是圆角，角上的点本来就落在外面）
        const centerY = box.top + box.height / 2;
        for (const x of [box.left + 2, box.left + box.width / 2, box.right - 2]) {
          const hit = document.elementFromPoint(x, centerY);
          expect(hit !== null && badge.contains(hit), `${label} hit at ${x},${centerY}`).toBe(true);
        }
      }
    });

    it('names the summary button after the user and lists every role in the panel', async () => {
      await renderRoles([withRoles('1', 'alice', LONG_ROLES)]);

      expect(rolesTrigger(0).getAttribute('aria-label')).toBe('View all roles of alice (13)');
      const panel = await openRoles(0);
      expect(panelLabels(panel)).toEqual(LONG_ROLES);
    });

    // 没有 "+N" 时被截断的名称也要读得到全文：整个摘要都是面板入口
    it('opens the full names from a cell with only one or two long roles', async () => {
      await renderRoles([
        withRoles('1', 'alice', [LONG_ROLES[0]]),
        withRoles('2', 'bob', LONG_ROLES.slice(0, 2)),
      ]);
      expect(host().querySelector('[data-testid="user-roles-more"]')).toBeNull();

      let panel = await openRoles(0);
      expect(panelLabels(panel)).toEqual([LONG_ROLES[0]]);
      await userEvent.keyboard('{Escape}');
      await expect
        .poll(() => document.querySelector('[data-testid="readonly-list-panel"]'))
        .toBeNull();

      panel = await openRoles(1);
      expect(panelLabels(panel)).toEqual(LONG_ROLES.slice(0, 2));
    });

    it('shows a dash instead of a panel for a user without roles', async () => {
      await renderRoles([user('1', 'alice')]);

      expect(rolesTrigger(0)).toBeUndefined();
    });

    describe('on coarse pointers', () => {
      afterEach(async () => {
        await setCoarsePointer(false);
      });

      // 伪元素把徽章行的命中区补到 44px：不被表格滚动区裁掉、与上下两行互不覆盖，点外沿打开的是本行的面板
      it('gives each summary button a 44px target that opens its own row', async () => {
        await setCoarsePointer(true);
        await renderRoles([
          withRoles('1', 'alice', LONG_ROLES.slice(0, 3)),
          withRoles('2', 'bob', LONG_ROLES.slice(3, 6)),
        ]);

        expectTouchTarget(rolesTrigger(0));
        expectTouchTarget(rolesTrigger(1));
        const height = rolesTrigger(1).getBoundingClientRect().height;
        await tapAt(rolesTrigger(1), 0, -(22 - height / 2) + 0.5);
        const panel = await expect
          .poll(() => document.querySelector<HTMLElement>('[data-testid="readonly-list-panel"]'))
          .not.toBeNull()
          .then(() => document.querySelector<HTMLElement>('[data-testid="readonly-list-panel"]')!);
        expect(panel.textContent).toContain('bob');
        expect(panelLabels(panel)).toEqual(LONG_ROLES.slice(3, 6));
      });
    });
  });

  describe('expanded row', () => {
    const LONG_EMAIL =
      'regional.compliance.reviewer.for.cross.border.settlement@subsidiary.example.test';
    const LONG_ROLE = 'Regional Compliance Reviewer for Cross-Border Settlement Operations';

    async function renderExpanded(
      tablet: boolean,
      row: UserManagementOutputDto = user('1', 'alice'),
    ): Promise<HTMLElement> {
      viewport.next(
        tablet
          ? {
              matches: true,
              breakpoints: { '(min-width: 768px)': true, '(min-width: 1024px)': false },
            }
          : desktop(false),
      );
      fixture.componentRef.setInput('users', [row]);
      const [table] = await fixture.getDeferBlocks();
      await table.render(DeferBlockState.Complete);
      fixture.autoDetectChanges();
      await fixture.whenStable();
      expandButtons()[0].click();
      await fixture.whenStable();
      return host().querySelector<HTMLElement>('[data-testid="user-detail"]')!;
    }

    /** 文字整段排在元素自己与所在详情格里，没有被截断或溢出到外面（徽章默认单行裁切）。 */
    function expectTextInside(element: HTMLElement, container: HTMLElement): void {
      const range = document.createRange();
      range.selectNodeContents(element);
      const text = range.getBoundingClientRect();
      for (const box of [element.getBoundingClientRect(), container.getBoundingClientRect()]) {
        expect(text.left).toBeGreaterThanOrEqual(box.left - 0.5);
        expect(text.right).toBeLessThanOrEqual(box.right + 0.5);
        expect(text.bottom).toBeLessThanOrEqual(box.bottom + 0.5);
      }
    }

    function detailColumns(detail: HTMLElement): (string | null)[] {
      return Array.from(detail.querySelectorAll('[data-detail-column]')).map((dd) =>
        dd.getAttribute('data-detail-column'),
      );
    }

    // 平板收起三级列（邮箱、创建时间），最近登录留在主行；手机再收起二级列
    it('lists the collapsed data columns in definition order', async () => {
      await page.viewport(1100, 800);
      onTestFinished(() => page.viewport(1280, 800));
      let detail = await renderExpanded(true);
      expect(detailColumns(detail)).toEqual(['email', 'creationTime']);

      viewport.next(desktop(false));
      await fixture.whenStable();
      detail = host().querySelector<HTMLElement>('[data-testid="user-detail"]')!;
      //#if (LocalIdentity)
      expect(detailColumns(detail)).toEqual(['email', 'roles', 'lastLoginTime', 'creationTime']);
      //#else
      expect(detailColumns(detail)).toEqual(['email', 'roles', 'creationTime']);
      //#endif
    });

    // 选择列由页面按批量操作打开、在窄屏仍保留；它不是数据列，开着关着都不进详情
    it('never lists the select or actions column, selectable or not', async () => {
      fixture.componentRef.setInput('selectable', true);
      let detail = await renderExpanded(false);
      expect(detailColumns(detail)).not.toContain('select');
      expect(detailColumns(detail)).not.toContain('actions');

      fixture.componentRef.setInput('selectable', false);
      await fixture.whenStable();
      detail = host().querySelector<HTMLElement>('[data-testid="user-detail"]')!;
      expect(detailColumns(detail)).not.toContain('select');
      expect(detail.textContent).not.toContain('users.batch.selectAll');
    });

    // 详情是完整内容：时刻带秒、角色全部列出且长名称整段可读、邮箱不截断
    it('gives the full time, every role and the whole email', async () => {
      await page.viewport(390, 844);
      onTestFinished(() => page.viewport(1280, 800));
      host().style.display = 'block';
      host().style.width = '356px';
      const detail = await renderExpanded(false, {
        ...user('1', 'alice'),
        email: LONG_EMAIL,
        creationTime: '2026-01-01T08:09:07Z',
        roles: [
          { id: 'r1', name: 'reviewer', displayName: LONG_ROLE },
          { id: 'r2', name: 'auditor', displayName: 'Auditor' },
          { id: 'r3', name: 'viewer', displayName: 'Viewer' },
        ],
      });
      const value = (id: string) =>
        detail.querySelector<HTMLElement>(`[data-detail-column="${id}"]`)!;

      expect(value('creationTime').textContent).toMatch(/:09:07/);

      const roles = Array.from(value('roles').querySelectorAll<HTMLElement>('[hlmBadge]'));
      expect(roles.map((badge) => badge.textContent!.trim())).toEqual([
        LONG_ROLE,
        'Auditor',
        'Viewer',
      ]);
      expectTextInside(roles[0], value('roles'));

      const email = value('email').firstElementChild as HTMLElement;
      expect(email.textContent!.trim()).toBe(LONG_EMAIL);
      expectTextInside(email, value('email'));
    });
    //#if (IncludeLocalization)

    // 表头与详情标题取同一份标签：切换语言时两处一起更新
    it('updates the header and the detail labels together when the language changes', async () => {
      const transloco = TestBed.inject(TranslocoService);
      transloco.setAvailableLangs(['en', 'zh-CN']);
      transloco.setTranslation({ table: { colEmail: 'Email' } }, 'users/en', { merge: true });
      transloco.setTranslation({ users: { table: { colEmail: '邮箱' } } }, 'zh-CN');
      const detail = await renderExpanded(false);
      const headers = () =>
        Array.from(host().querySelectorAll('th')).map((th) => th.textContent!.trim());
      const labels = () =>
        Array.from(detail.querySelectorAll('dt')).map((dt) => dt.textContent!.trim());
      expect(labels()).toContain('Email');

      transloco.setActiveLang('zh-CN');
      await fixture.whenStable();
      expect(labels()).toContain('邮箱');

      await page.viewport(1800, 900);
      onTestFinished(() => page.viewport(1280, 800));
      viewport.next(desktop(true));
      await expect.poll(() => headers()).toContain('邮箱');
    });
    //#endif
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function rowMenuTrigger(): HTMLButtonElement | null {
    const icon = host().querySelector('tbody ng-icon[name="lucideEllipsis"]');
    return icon?.closest('button') ?? null;
  }

  /** 菜单经 CDK 浮层挂在 document 上，不在组件宿主里。 */
  async function openRowMenu(): Promise<HTMLElement> {
    rowMenuTrigger()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    return document.querySelector<HTMLElement>('[data-slot="dropdown-menu"]')!;
  }

  /** 以 currentUserId 登录、表格只有一行（默认 alice，id 1）时，打开那一行的菜单。 */
  async function openMenuAs(
    currentUserId: string,
    row: UserManagementOutputDto = user('1', 'alice'),
    canManageRoles = false,
  ): Promise<HTMLElement> {
    currentUser.set({ id: currentUserId });
    fixture.componentRef.setInput('users', [row]);
    fixture.componentRef.setInput('canManageRoles', canManageRoles);
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();
    onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());
    return openRowMenu();
  }

  /** 停用项的图标是属性绑定、不反射成 attribute，按文案（或未装词条时的键名）找。 */
  function menuButton(menu: HTMLElement, label: RegExp): HTMLButtonElement {
    return Array.from(menu.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
      label.test(button.textContent ?? ''),
    )!;
  }

  /** 行内图标按钮（编辑、分配角色）；菜单在 CDK 浮层里，不在宿主中。 */
  function inlineButtons(): HTMLButtonElement[] {
    return Array.from(
      host().querySelectorAll(
        'tbody ng-icon[name="lucidePencil"], tbody ng-icon[name="lucideShieldCheck"]',
      ),
    ).map((icon) => icon.closest('button')!);
  }

  function expandButtons(): HTMLButtonElement[] {
    const host = fixture.nativeElement as HTMLElement;
    return Array.from(host.querySelectorAll('tbody ng-icon[name="lucideChevronRight"]')).map(
      (icon) => icon.closest('button')!,
    );
  }

  function expandedStates(): (string | null)[] {
    return expandButtons().map((button) => button.getAttribute('aria-expanded'));
  }
});
