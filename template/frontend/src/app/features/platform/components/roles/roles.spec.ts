import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
// prettier-ignore
import {
  signal,
  //#if (IncludeRealTime)
  type DestroyRef,
  //#endif
} from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { toast } from '@spartan-ng/brain/sonner';
import { PaginationState, SortingState } from '@tanstack/angular-table';
import { of, Subject, throwError } from 'rxjs';

import { Roles } from './roles';
import { RoleTable } from './widgets/role-table/role-table';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AccountPreferencesService } from '../../../../core/services/account-preferences-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
//#if (IncludeRealTime)
import { SignalRService } from '../../../../core/services/signalr-service';
//#endif
import { StartupService } from '../../../../core/services/startup-service';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
import { GetRolesInputDto, RoleOutputDto } from '../../dtos/role.dto';
import { RoleService } from '../../services/role-service';

import type { MockedObject } from 'vitest';

/** 角色页面的查询闭环；与用户页各自实现，因此各写一份。 */
describe('Roles page query round trip', () => {
  let fixture: ComponentFixture<Roles>;
  let component: Roles;
  let router: Router;
  let service: Pick<MockedObject<RoleService>, 'getRoles' | 'deleteRole'>;
  //#if (IncludeRealTime)
  const realtime = {
    lastResourceEvent: signal<{ eventName: string; payload: unknown } | null>(null),
    registerResourceEvent: vi.fn(),
    connect: vi.fn(() => Promise.resolve()),
    watchResource: vi.fn<(resourceKey: string, destroyRef?: DestroyRef) => void>(),
    resourceSubscribed$: new Subject<string>(),
  };
  //#endif

  function lastQuery(): GetRolesInputDto {
    const calls = vi.mocked(service.getRoles).mock.calls;
    const query = calls.at(-1)?.[0];
    if (!query) {
      throw new Error('The list request was never sent');
    }

    return query;
  }

  /** 真实的子表实例：事件必须从它的 output 发出，才能覆盖模板里的绑定名。 */
  function table(): RoleTable {
    return fixture.debugElement.query(By.directive(RoleTable)).componentInstance as RoleTable;
  }

  beforeEach(async () => {
    service = {
      getRoles: vi.fn().mockName('RoleService.getRoles'),
      deleteRole: vi.fn().mockReturnValue(of(undefined)),
    };
    service.getRoles.mockReturnValue(of({ items: [], totalCount: 0 }) as never);

    await TestBed.configureTestingModule({
      imports: [Roles],
      providers: [
        provideRouter([{ path: 'platform/roles', children: [] }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: RoleService, useValue: service },
        { provide: StartupService, useValue: { status: signal('success' as const) } },
        //#if (IncludeRealTime)
        { provide: SignalRService, useValue: realtime },
        //#endif
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    await router.navigate(['/platform/roles']);

    TestBed.inject(AuthorizationService).setPermissions({
      permissions: [PERMISSIONS.roles.default],
      isSuperAdmin: false,
      versionToken: 'r1',
    });

    // 先建好设置上下文（连带语言服务），与应用启动一致；否则子表格首次渲染时才注入它，
    // 激活语言会让外层 *transloco 在视图未建完时重建。
    TestBed.inject(AccountPreferencesService);
    fixture = TestBed.createComponent(Roles);
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

    table().sortingChange.emit([{ id: 'displayName', desc: true }] as SortingState);
    await fixture.whenStable();

    // 停在第 3 页换排序，看到的是另一批数据的第 3 页，等于结果错乱。
    expect(router.url).toContain('page=1');
    expect(lastQuery().offset).toBe(0);
    expect(lastQuery().sorting).toBeTruthy();
  });

  it('restores component state from the URL on reload and back/forward navigation', async () => {
    await router.navigate(['/platform/roles'], {
      queryParams: { page: 2, pageSize: 50, keyword: 'admin' },
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.pagination()).toEqual(expect.objectContaining({ pageIndex: 1, pageSize: 50 }));
    expect(lastQuery().keyword).toBe('admin');
  });

  it('keeps loading on while a superseded request is cancelled and the new one is in flight', async () => {
    const first = new Subject<{ items: RoleOutputDto[]; totalCount: number }>();
    const second = new Subject<{ items: RoleOutputDto[]; totalCount: number }>();
    service.getRoles.mockReturnValueOnce(first as never).mockReturnValueOnce(second as never);

    table().paginationChange.emit({ pageIndex: 1, pageSize: 20 } as PaginationState);
    await fixture.whenStable();
    expect(component.loading()).toBe(true);

    // 第一页还没返回就翻到下一页：旧请求被取消，它的 finalize 不能把新请求的加载状态关掉。
    table().paginationChange.emit({ pageIndex: 2, pageSize: 20 } as PaginationState);
    await fixture.whenStable();
    expect(component.loading()).toBe(true);

    second.next({ items: [], totalCount: 0 });
    second.complete();
    expect(component.loading()).toBe(false);
  });
  //#if (IncludeRealTime)

  it('subscribes to the role list of its own scope and refetches when it changes', async () => {
    await fixture.whenStable();
    // 宿主用户：作用域段为 host，与后端 ICurrentTenant.ScopeKey 一致
    expect(realtime.registerResourceEvent).toHaveBeenCalledWith('Roles.Changed');
    expect(realtime.watchResource).toHaveBeenCalledWith('host:roles', expect.anything());
    expect(realtime.connect).toHaveBeenCalled();

    const before = vi.mocked(service.getRoles).mock.calls.length;
    realtime.lastResourceEvent.set({ eventName: 'Roles.Changed', payload: {} });
    fixture.detectChanges();
    await fixture.whenStable();

    expect(vi.mocked(service.getRoles).mock.calls.length).toBe(before + 1);
  });

  it('refetches once its own role list subscription is confirmed, such as after reconnecting', async () => {
    await fixture.whenStable();
    const before = vi.mocked(service.getRoles).mock.calls.length;

    // 断线期间的变更不会补推：订阅恢复确认后补查一次
    realtime.resourceSubscribed$.next('host:roles');
    await fixture.whenStable();
    expect(vi.mocked(service.getRoles).mock.calls.length).toBe(before + 1);

    // 别的资源恢复与本页无关
    realtime.resourceSubscribed$.next('host:users');
    await fixture.whenStable();
    expect(vi.mocked(service.getRoles).mock.calls.length).toBe(before + 1);
  });

  it('holds the subscription for exactly the lifetime of the page', async () => {
    await fixture.whenStable();
    // 交给服务的是本页的 DestroyRef：页面销毁即撤销，连接前离开也由服务保证不再订阅
    const destroyRef = realtime.watchResource.mock.calls.at(-1)?.[1];
    expect(destroyRef?.destroyed).toBe(false);

    fixture.destroy();

    expect(destroyRef?.destroyed).toBe(true);
  });
  //#endif

  /** 加载成功时的一行：表格会真的渲染它，字段要齐。 */
  const loadedRow = {
    id: 'row-1',
    name: 'auditor',
    displayName: 'Auditor',
    isDefault: false,
    isStatic: false,
    sort: 1,
    userCount: 0,
    permissionCount: 0,
    creationTime: '2026-01-01T00:00:00Z',
  };

  /** 以首次请求失败重建页面：此时没有任何旧行可保留。 */
  async function openWithFailedFirstLoad(): Promise<void> {
    fixture.destroy();
    //#if (IncludeRealTime)
    // 推送桩在用例间共享：前面用例留下的"角色已变更"会让新页面多补查一次
    realtime.lastResourceEvent.set(null);
    //#endif
    service.getRoles.mockReturnValue(throwError(() => new Error('boom')) as never);
    fixture = TestBed.createComponent(Roles);
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
    expect(component.roles()).toEqual([]);
    expect(notify).not.toHaveBeenCalled();

    const requests = service.getRoles.mock.calls.length;
    service.getRoles.mockReturnValue(of({ items: [loadedRow], totalCount: 1 }) as never);
    table().retry.emit();
    await fixture.whenStable();

    expect(service.getRoles).toHaveBeenCalledTimes(requests + 1);
    expect(component.loadError()).toBeNull();
    expect(component.roles()).toHaveLength(1);
  });

  it('keeps the loaded rows and only notifies when a refresh fails', async () => {
    const notify = vi.spyOn(toast, 'error').mockImplementation(() => '');
    service.getRoles.mockReturnValue(of({ items: [loadedRow], totalCount: 1 }) as never);
    component.reload();
    await fixture.whenStable();

    service.getRoles.mockReturnValue(throwError(() => new Error('boom')) as never);
    component.reload();
    await fixture.whenStable();

    expect(component.roles()).toHaveLength(1);
    expect(component.loadError()).toBeNull();
    expect(notify).toHaveBeenCalledOnce();
  });
  it('locks delete before confirmation and unlocks after cancellation', async () => {
    let answer!: (value: boolean) => void;
    const confirm = vi
      .spyOn(TestBed.inject(ConfirmService), 'open')
      .mockReturnValue(new Promise<boolean>((resolve) => (answer = resolve)));
    const pending = component.onDelete(loadedRow);
    const duplicate = component.onDelete(loadedRow);
    fixture.detectChanges();
    try {
      expect(confirm).toHaveBeenCalledOnce();
      expect(component.deletingId()).toBeTruthy();
      expect(table().deletingId()).toBe(component.deletingId());
    } finally {
      answer(false);
      await Promise.all([pending, duplicate]);
    }
    expect(component.deletingId()).toBeFalsy();
  });

  it('keeps the confirmed write alive and busy after its component is destroyed', async () => {
    const response = new Subject<void>();
    service.deleteRole.mockReturnValue(response);
    vi.spyOn(TestBed.inject(ConfirmService), 'open').mockResolvedValue(true);
    const notify = vi.spyOn(toast, 'success').mockImplementation(() => '');
    const pending = component.onDelete(loadedRow);
    await Promise.resolve();
    expect(component.deletingId()).toBe(loadedRow.id);
    expect(service.deleteRole).toHaveBeenCalledOnce();
    await component.onDelete(loadedRow);
    expect(service.deleteRole).toHaveBeenCalledOnce();
    fixture.destroy();
    expect(response.observed).toBe(true);
    response.next();
    response.complete();
    await pending;
    expect(component.deletingId()).toBeNull();
    expect(notify).toHaveBeenCalledOnce();
  });

  // 页面骨架（各列表页同一写法，这里以角色页为代表）：滚动列的直接子项不收缩。
  // 带 overflow-hidden 的子项（如直接放进来的表格外框）否则会被压到剩余高度，内容被裁且滚不到
  it('never squeezes a clipping child of the scroll column', () => {
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.height = '400px';
    const column = host.querySelector<HTMLElement>('.overflow-y-auto')!;
    const frame = document.createElement('div');
    frame.style.overflow = 'hidden';
    const content = document.createElement('div');
    content.style.height = '1200px';
    frame.append(content);
    column.append(frame);

    expect(frame.getBoundingClientRect().height).toBe(1200);
    expect(column.scrollHeight).toBeGreaterThan(column.clientHeight);
  });
});
