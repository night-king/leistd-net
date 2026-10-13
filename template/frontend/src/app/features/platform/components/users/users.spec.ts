import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { toast } from '@spartan-ng/brain/sonner';
import { PaginationState, SortingState } from '@tanstack/angular-table';
import { of, Subject, throwError } from 'rxjs';

import { Users } from './users';
import { UserTable } from './widgets/user-table/user-table';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { StartupService } from '../../../../core/services/startup-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
import { GetUsersInputDto } from '../../dtos/user-management.dto';
import { UserManagementService } from '../../services/user-management-service';

import type { MockedObject } from 'vitest';

/** 用户页面的查询闭环：子表事件 → URL query → 列表请求参数，覆盖子表用例测不到的模板绑定与映射。 */
describe('Users page query round trip', () => {
  let fixture: ComponentFixture<Users>;
  let component: Users;
  let router: Router;
  let service: Pick<MockedObject<UserManagementService>, 'getUsers' | 'batchDisableUsers'>;

  function lastQuery(): GetUsersInputDto {
    const calls = vi.mocked(service.getUsers).mock.calls;
    const query = calls.at(-1)?.[0];
    if (!query) {
      throw new Error('The list request was never sent');
    }

    return query;
  }

  /** 真实的子表实例：事件必须从它的 output 发出，才能覆盖模板里的绑定名。 */
  function table(): UserTable {
    return fixture.debugElement.query(By.directive(UserTable)).componentInstance as UserTable;
  }

  beforeEach(async () => {
    service = {
      getUsers: vi.fn().mockName('UserManagementService.getUsers'),
      batchDisableUsers: vi.fn().mockName('UserManagementService.batchDisableUsers'),
    };
    service.getUsers.mockReturnValue(of({ items: [], totalCount: 0 }) as never);

    await TestBed.configureTestingModule({
      imports: [Users],
      providers: [
        provideRouter([{ path: 'platform/users', children: [] }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { currentUser: signal(null) } },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: UserManagementService, useValue: service },
        { provide: ConfirmService, useValue: { open: () => Promise.resolve(true) } },
        { provide: StartupService, useValue: { status: signal('success' as const) } },
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    await router.navigate(['/platform/users']);

    TestBed.inject(AuthorizationService).setPermissions({
      permissions: [PERMISSIONS.users.default],
      isSuperAdmin: false,
      versionToken: 'r1',
    });

    // 先建好设置上下文（连带语言服务），与应用启动一致；否则子表格首次渲染时才注入它，
    // 激活语言会让外层 *transloco 在视图未建完时重建。
    TestBed.inject(SettingContextService);
    fixture = TestBed.createComponent(Users);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('writes paging to the URL and refetches the new page', async () => {
    table().paginationChange.emit({ pageIndex: 2, pageSize: 20 } as PaginationState);
    await fixture.whenStable();

    // 页码在 URL 里是 1 基（可分享、可前进后退），发给接口的是 offset。
    expect(router.url).toContain('page=3');
    expect(lastQuery().offset).toBe(40);
    expect(lastQuery().limit).toBe(20);
  });

  it('resets to the first page and offset 0 when rows per page changes', async () => {
    table().paginationChange.emit({ pageIndex: 3, pageSize: 20 } as PaginationState);
    await fixture.whenStable();

    table().paginationChange.emit({ pageIndex: 0, pageSize: 50 } as PaginationState);
    await fixture.whenStable();

    expect(lastQuery().offset).toBe(0);
    expect(lastQuery().limit).toBe(50);
  });

  it('writes sorting to the URL, resets to page one and maps it to the API sort', async () => {
    table().paginationChange.emit({ pageIndex: 2, pageSize: 20 } as PaginationState);
    await fixture.whenStable();

    table().sortingChange.emit([{ id: 'username', desc: true }] as SortingState);
    await fixture.whenStable();

    // 停在第 3 页换排序，看到的是另一批数据的第 3 页，等于结果错乱。
    expect(router.url).toContain('page=1');
    expect(lastQuery().offset).toBe(0);
    expect(lastQuery().sorting).toBe('username desc');
  });
  //#if (LocalIdentity)

  it('maps the last login table column to the entity path while preserving URL state', async () => {
    table().sortingChange.emit([{ id: 'lastLoginTime', desc: true }]);
    await fixture.whenStable();

    expect(router.url).toContain('sort=lastLoginTime');
    expect(lastQuery().sorting).toBe('lastLogin.Time desc');
  });

  //#endif
  // 启停用户在所有形态都受 Users.Update 保护，表格的更新开关必须跟随该权限
  it('passes the update permission to the table', () => {
    expect(table().canUpdate()).toBe(false);

    TestBed.inject(AuthorizationService).setPermissions({
      permissions: [PERMISSIONS.users.default, PERMISSIONS.users.update],
      isSuperAdmin: false,
      versionToken: 'r2',
    });
    fixture.detectChanges();

    expect(table().canUpdate()).toBe(true);
  });

  it('uses the DTO default when the URL contains an unknown sort column', async () => {
    await router.navigate(['/platform/users'], {
      queryParams: { sort: 'passwordHash', direction: 'asc' },
    });
    await fixture.whenStable();

    expect(lastQuery().sorting).toBe('creationTime desc');
  });

  it('restores component state from the URL on reload and back/forward navigation', async () => {
    await router.navigate(['/platform/users'], {
      queryParams: { page: 2, pageSize: 50, keyword: 'alice' },
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.pagination()).toEqual(expect.objectContaining({ pageIndex: 1, pageSize: 50 }));
    expect(lastQuery().keyword).toBe('alice');
  });

  it('keeps loading on while a superseded request is cancelled and the new one is in flight', async () => {
    const first = new Subject<never>();
    const second = new Subject<{ items: never[]; totalCount: number }>();
    service.getUsers.mockReturnValueOnce(first as never).mockReturnValueOnce(second as never);

    table().paginationChange.emit({ pageIndex: 1, pageSize: 20 } as PaginationState);
    await fixture.whenStable();
    expect(component.loading()).toBe(true);

    // 上一页还没返回就翻页：旧请求被取消，它的 finalize 不能把新请求的加载状态关掉。
    table().paginationChange.emit({ pageIndex: 2, pageSize: 20 } as PaginationState);
    await fixture.whenStable();
    expect(component.loading()).toBe(true);

    second.next({ items: [], totalCount: 0 });
    second.complete();
    expect(component.loading()).toBe(false);
  });

  /** 加载成功时的一行：表格会真的渲染它，字段要齐。 */
  const loadedRow = {
    id: 'row-1',
    username: 'alice',
    email: 'alice@example.test',
    displayName: 'Alice',
    isActive: true,
    isSuperAdmin: false,
    roles: [],
    creationTime: '2026-01-01T00:00:00Z',
  };

  /** 以首次请求失败重建页面：此时没有任何旧行可保留。 */
  async function openWithFailedFirstLoad(): Promise<void> {
    fixture.destroy();
    service.getUsers.mockReturnValue(throwError(() => new Error('boom')) as never);
    fixture = TestBed.createComponent(Users);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('shows the load failure with retry instead of the empty state when the first load fails', async () => {
    const notify = vi.spyOn(toast, 'error').mockImplementation(() => '');
    await openWithFailedFirstLoad();

    // 没有旧行时不能落到"暂无数据"：表格拿到失败原因，显示错误态与重试
    expect(component.loadError()).toBe('boom');
    expect(table().loadError()).toBe('boom');
    expect(component.users()).toEqual([]);
    expect(notify).not.toHaveBeenCalled();

    const requests = service.getUsers.mock.calls.length;
    service.getUsers.mockReturnValue(of({ items: [loadedRow], totalCount: 1 }) as never);
    table().retry.emit();
    await fixture.whenStable();

    expect(service.getUsers).toHaveBeenCalledTimes(requests + 1);
    expect(component.loadError()).toBeNull();
    expect(component.users()).toHaveLength(1);
  });

  it('keeps the loaded rows and only notifies when a refresh fails', async () => {
    const notify = vi.spyOn(toast, 'error').mockImplementation(() => '');
    service.getUsers.mockReturnValue(of({ items: [loadedRow], totalCount: 1 }) as never);
    component.reloadList();
    await fixture.whenStable();

    service.getUsers.mockReturnValue(throwError(() => new Error('boom')) as never);
    component.reloadList();
    await fixture.whenStable();

    expect(component.users()).toHaveLength(1);
    expect(component.loadError()).toBeNull();
    expect(notify).toHaveBeenCalledOnce();
  });

  it('renders the failure reason and a retry button that refetches, not the empty state', async () => {
    await openWithFailedFirstLoad();
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [deferred] = await fixture.getDeferBlocks();
    await deferred.render(DeferBlockState.Complete);

    const host = fixture.nativeElement as HTMLElement;
    const retry = host.querySelector<HTMLButtonElement>('[data-testid="table-retry"]');
    expect(retry).not.toBeNull();
    expect(host.textContent).toContain('boom');
    expect(host.textContent).not.toMatch(/No users yet|users\.table\.emptyTitle/);

    const requests = service.getUsers.mock.calls.length;
    service.getUsers.mockReturnValue(of({ items: [], totalCount: 0 }) as never);
    retry?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    // 重试成功且确实为空：这时才显示"暂无数据"
    expect(service.getUsers).toHaveBeenCalledTimes(requests + 1);
    expect(host.querySelector('[data-testid="table-retry"]')).toBeNull();
    expect(host.textContent).toMatch(/No users yet|users\.table\.emptyTitle/);
  });

  describe('batch operations', () => {
    function grant(...permissions: string[]): void {
      TestBed.inject(AuthorizationService).setPermissions({
        permissions: [PERMISSIONS.users.default, ...permissions],
        isSuperAdmin: false,
        versionToken: `r-${permissions.join()}`,
      });
      fixture.detectChanges();
    }

    it('shows the selection column only when a bulk action is permitted', () => {
      expect(table().selectable()).toBe(false);

      grant(PERMISSIONS.users.manageRoles);

      expect(table().selectable()).toBe(true);
    });

    const secondRow = { ...loadedRow, id: 'row-2', username: 'bob', displayName: 'Bob' };

    async function loadRows(...rows: (typeof loadedRow)[]): Promise<void> {
      service.getUsers.mockReturnValue(of({ items: rows, totalCount: rows.length }) as never);
      component.reloadList();
      await fixture.whenStable();
    }

    it('clears the selection when the page, filter or sorting changes', async () => {
      grant(PERMISSIONS.users.update);
      await loadRows(loadedRow);
      table().rowSelectionChange.emit({ 'row-1': true });
      expect(component.selectedIds()).toEqual(['row-1']);

      table().paginationChange.emit({ pageIndex: 1, pageSize: 20 } as PaginationState);
      await fixture.whenStable();

      expect(component.selectedIds()).toEqual([]);
    });

    it('clears the selection on Esc even when focus is on the page body', async () => {
      grant(PERMISSIONS.users.update);
      await loadRows(loadedRow);
      table().rowSelectionChange.emit({ 'row-1': true });

      document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

      expect(component.selectedIds()).toEqual([]);
    });

    it('keeps the selection when Esc was handled by an overlay or typed in a field', async () => {
      grant(PERMISSIONS.users.update);
      await loadRows(loadedRow);
      table().rowSelectionChange.emit({ 'row-1': true });

      const handled = new KeyboardEvent('keydown', {
        key: 'Escape',
        bubbles: true,
        cancelable: true,
      });
      handled.preventDefault();
      document.body.dispatchEvent(handled);
      const input = document.createElement('input');
      document.body.append(input);
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      input.remove();

      expect(component.selectedIds()).toEqual(['row-1']);
    });

    it('keeps the selection on Esc while a menu opened by mouse is still showing', async () => {
      grant(PERMISSIONS.users.update);
      await loadRows(loadedRow);
      table().rowSelectionChange.emit({ 'row-1': true });
      const overlay = document.createElement('div');
      overlay.className = 'cdk-overlay-container';
      overlay.innerHTML = '<div role="menu"></div>';
      document.body.append(overlay);

      document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      overlay.remove();

      expect(component.selectedIds()).toEqual(['row-1']);
    });

    it('reports partial failures, clears the selection and reloads the page', async () => {
      grant(PERMISSIONS.users.update);
      await loadRows(loadedRow, secondRow);
      service.batchDisableUsers.mockReturnValue(
        of({
          items: [
            { id: 'row-1', succeeded: true },
            {
              id: 'row-2',
              succeeded: false,
              errorCode: 'User:SelfDisableForbidden',
              message: 'No',
            },
          ],
        }) as never,
      );
      const warning = vi.spyOn(toast, 'warning');
      const requests = service.getUsers.mock.calls.length;
      table().rowSelectionChange.emit({ 'row-1': true, 'row-2': true });

      await component.batchSetActive(false);

      expect(service.batchDisableUsers).toHaveBeenCalledWith({ ids: ['row-1', 'row-2'] });
      expect(warning).toHaveBeenCalledOnce();
      expect(component.selectedIds()).toEqual([]);
      expect(service.getUsers.mock.calls.length).toBe(requests + 1);
    });

    it('does not let rows of the previous page be selected while the next page loads', async () => {
      grant(PERMISSIONS.users.update);
      await loadRows(loadedRow);
      const nextPage = new Subject<{ items: (typeof loadedRow)[]; totalCount: number }>();
      service.getUsers.mockReturnValue(nextPage as never);

      table().paginationChange.emit({ pageIndex: 1, pageSize: 20 } as PaginationState);
      await fixture.whenStable();
      // 表格包在 @defer 里，测试里要显式渲染
      const [tableBlock] = await fixture.getDeferBlocks();
      await tableBlock.render(DeferBlockState.Complete);
      const selectAll = fixture.nativeElement.querySelector(
        'thead [role="checkbox"]',
      ) as HTMLButtonElement;
      selectAll.click();
      fixture.detectChanges();

      expect(selectAll.disabled).toBe(true);
      expect(component.selectedIds()).toEqual([]);

      // 即使选择状态里残留了旧行，新页到达后也不会把它们当作批量目标
      table().rowSelectionChange.emit({ 'row-1': true });
      nextPage.next({ items: [secondRow], totalCount: 21 });
      nextPage.complete();
      await fixture.whenStable();

      expect(component.selectedIds()).toEqual([]);
      expect(component.rowSelection()).toEqual({});
    });
  });
});
