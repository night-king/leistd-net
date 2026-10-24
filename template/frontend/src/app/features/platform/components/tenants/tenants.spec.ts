import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { toast } from '@spartan-ng/brain/sonner';
import { PaginationState } from '@tanstack/angular-table';
import { of, Subject, throwError } from 'rxjs';

import { Tenants } from './tenants';
import { TenantEditDialog } from './widgets/tenant-edit-dialog/tenant-edit-dialog';
import { TenantTable } from './widgets/tenant-table/tenant-table';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { StartupService } from '../../../../core/services/startup-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
import { CreateTenantInputDto, GetTenantsInputDto, TenantOutputDto } from '../../dtos/tenant.dto';
import { TenantService } from '../../services/tenant-service';

import type { MockedObject } from 'vitest';

/** 租户页面的查询与写操作闭环；与用户、角色页各自实现，因此各写一份。 */
describe('Tenants page query and write flow', () => {
  let fixture: ComponentFixture<Tenants>;
  let component: Tenants;
  let router: Router;
  let service: Pick<
    MockedObject<TenantService>,
    'getTenants' | 'createTenant' | 'updateTenant' | 'setActivation' | 'deleteTenant'
  >;
  let confirm: Pick<MockedObject<ConfirmService>, 'open'>;

  const tenant: TenantOutputDto = {
    id: '019ff8ed-221b-7673-9ba8-6b6dd5a638ab',
    name: 'acme',
    displayName: 'Acme Inc.',
    isActive: true,
    creationTime: '2026-08-14T00:00:00Z',
  };

  function lastQuery(): GetTenantsInputDto {
    const calls = vi.mocked(service.getTenants).mock.calls;
    const query = calls.at(-1)?.[0];
    if (!query) {
      throw new Error('The list request was never sent');
    }

    return query;
  }

  /** 真实的子表实例：事件必须从它的 output 发出，才能覆盖模板里的绑定名。 */
  function table(): TenantTable {
    return fixture.debugElement.query(By.directive(TenantTable)).componentInstance as TenantTable;
  }

  /** 真实的编辑对话框实例：同理，保存事件要从它发出才覆盖 (save) 绑定。 */
  function editDialog(): TenantEditDialog {
    return fixture.debugElement.query(By.directive(TenantEditDialog))
      .componentInstance as TenantEditDialog;
  }

  const newTenantPayload: CreateTenantInputDto = {
    name: 'globex',
    displayName: 'Globex Corp.',
    adminEmail: 'admin@globex.example.com',
    adminPassword: 'Globex@123456',
  };

  beforeEach(async () => {
    service = {
      getTenants: vi.fn().mockName('TenantService.getTenants'),
      createTenant: vi.fn().mockName('TenantService.createTenant'),
      updateTenant: vi.fn().mockName('TenantService.updateTenant'),
      setActivation: vi.fn().mockName('TenantService.setActivation'),
      deleteTenant: vi.fn().mockName('TenantService.deleteTenant'),
    };
    service.getTenants.mockReturnValue(of({ items: [tenant], totalCount: 1 }) as never);
    service.createTenant.mockReturnValue(of(tenant) as never);
    service.updateTenant.mockReturnValue(of(tenant) as never);
    service.setActivation.mockReturnValue(of(tenant) as never);
    service.deleteTenant.mockReturnValue(of(undefined) as never);

    confirm = {
      open: vi.fn().mockName('ConfirmService.open'),
    };
    confirm.open.mockResolvedValue(true);

    await TestBed.configureTestingModule({
      imports: [Tenants],
      providers: [
        provideRouter([{ path: 'platform/tenants', children: [] }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: TenantService, useValue: service },
        { provide: ConfirmService, useValue: confirm },
        { provide: StartupService, useValue: { status: signal('success' as const) } },
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    await router.navigate(['/platform/tenants']);

    TestBed.inject(AuthorizationService).setPermissions({
      permissions: [
        PERMISSIONS.tenants.default,
        PERMISSIONS.tenants.update,
        PERMISSIONS.tenants.delete,
      ],
      isSuperAdmin: false,
      versionToken: 'r1',
    });

    // 先建好设置上下文（连带语言服务），否则首帧渲染途中创建它会让结构指令重入。
    TestBed.inject(SettingContextService);
    fixture = TestBed.createComponent(Tenants);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    try {
      fixture.destroy();
    } finally {
      vi.clearAllTimers();
      vi.useRealTimers();
    }
  });

  it('writes paging to the URL and refetches with the new page', async () => {
    table().paginationChange.emit({ pageIndex: 2, pageSize: 20 } as PaginationState);
    await fixture.whenStable();

    // 页码在 URL 里是 1 基（可分享、可前进后退），发给接口的是 offset。
    expect(router.url).toContain('page=3');
    expect(lastQuery().offset).toBe(40);
    expect(lastQuery().limit).toBe(20);
  });

  it('restores component state from the URL on reload and back/forward navigation', async () => {
    await router.navigate(['/platform/tenants'], {
      queryParams: { page: 2, pageSize: 50, keyword: 'acme' },
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.pagination()).toEqual(expect.objectContaining({ pageIndex: 1, pageSize: 50 }));
    expect(lastQuery().keyword).toBe('acme');
  });

  it('writes the debounced search to the URL and returns to the first page', async () => {
    table().paginationChange.emit({ pageIndex: 3, pageSize: 20 } as PaginationState);
    await fixture.whenStable();

    const queries = vi.mocked(service.getTenants).mock.calls.length;
    vi.useFakeTimers({
      toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
    });
    component.onSearchQueryChange('acme');
    await vi.advanceTimersByTimeAsync(299);
    expect(vi.mocked(service.getTenants).mock.calls.length).toBe(queries);
    expect(router.url).not.toContain('keyword=acme');
    expect(lastQuery().offset).toBe(60);
    await vi.advanceTimersByTimeAsync(1);
    await fixture.whenStable();

    // 停在第 4 页换关键字，看到的是另一批结果的第 4 页，等于结果错乱。
    expect(router.url).toContain('keyword=acme');
    expect(lastQuery().offset).toBe(0);
  });

  it('toggles activation via the API after confirmation and refreshes the list', async () => {
    const before = vi.mocked(service.getTenants).mock.calls.length;

    table().toggleActive.emit(tenant);
    await fixture.whenStable();

    expect(confirm.open).toHaveBeenCalled();
    expect(service.setActivation).toHaveBeenCalledWith(tenant.id, false);
    expect(vi.mocked(service.getTenants).mock.calls.length).toBeGreaterThan(before);
  });

  it('saves a new tenant via the create API, then closes the dialog and refreshes the list', async () => {
    const before = vi.mocked(service.getTenants).mock.calls.length;

    component.openCreate();
    fixture.detectChanges();

    editDialog().save.emit(newTenantPayload);
    await fixture.whenStable();

    // 创建与更新走同一个 (save) 出口，选错分支会把新建打成"更新一个不存在的租户"
    expect(service.createTenant).toHaveBeenCalledWith(newTenantPayload);
    expect(service.updateTenant).not.toHaveBeenCalled();

    expect(component.editDialogOpen()).toBe(false);
    expect(vi.mocked(service.getTenants).mock.calls.length).toBeGreaterThan(before);
  });

  it('saves an edit via the update API with the Id of the edited tenant', async () => {
    component.openEdit(tenant);
    fixture.detectChanges();

    const payload = { name: 'acme', displayName: 'Acme Renamed' };
    editDialog().save.emit(payload);
    await fixture.whenStable();

    expect(service.updateTenant).toHaveBeenCalledWith(tenant.id, payload);
    expect(service.createTenant).not.toHaveBeenCalled();
    expect(component.editDialogOpen()).toBe(false);
  });

  it('keeps the dialog open without losing user input when saving fails', async () => {
    service.createTenant.mockReturnValue(throwError(() => new Error('boom')) as never);

    component.openCreate();
    fixture.detectChanges();

    editDialog().save.emit(newTenantPayload);
    await fixture.whenStable();

    // 关掉对话框等于连同用户填的表单一起丢掉，只能报错并留在原地
    expect(component.editDialogOpen()).toBe(true);
  });

  it('deletes via the API after confirmation and refreshes the list', async () => {
    const before = vi.mocked(service.getTenants).mock.calls.length;

    table().delete.emit(tenant);
    await fixture.whenStable();

    expect(service.deleteTenant).toHaveBeenCalledWith(tenant.id);
    expect(vi.mocked(service.getTenants).mock.calls.length).toBeGreaterThan(before);
  });

  it('does not call the API when the delete confirmation is cancelled', async () => {
    confirm.open.mockResolvedValue(false);

    table().delete.emit(tenant);
    await fixture.whenStable();

    expect(service.deleteTenant).not.toHaveBeenCalled();
  });

  it('keeps loading on while a superseded request is cancelled and the new one is in flight', async () => {
    const first = new Subject<never>();
    const second = new Subject<{ items: never[]; totalCount: number }>();
    service.getTenants.mockReturnValueOnce(first as never).mockReturnValueOnce(second as never);

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

  /** 以首次请求失败重建页面：此时没有任何旧行可保留。 */
  async function openWithFailedFirstLoad(): Promise<void> {
    fixture.destroy();
    service.getTenants.mockReturnValue(throwError(() => new Error('boom')) as never);
    fixture = TestBed.createComponent(Tenants);
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
    expect(component.tenants()).toEqual([]);
    expect(notify).not.toHaveBeenCalled();

    const requests = service.getTenants.mock.calls.length;
    service.getTenants.mockReturnValue(of({ items: [tenant], totalCount: 1 }) as never);
    table().retry.emit();
    await fixture.whenStable();

    expect(service.getTenants).toHaveBeenCalledTimes(requests + 1);
    expect(component.loadError()).toBeNull();
    expect(component.tenants()).toHaveLength(1);
  });

  it('keeps the loaded rows and only notifies when a refresh fails', async () => {
    const notify = vi.spyOn(toast, 'error').mockImplementation(() => '');
    service.getTenants.mockReturnValue(of({ items: [tenant], totalCount: 1 }) as never);
    component.reload();
    await fixture.whenStable();

    service.getTenants.mockReturnValue(throwError(() => new Error('boom')) as never);
    component.reload();
    await fixture.whenStable();

    expect(component.tenants()).toHaveLength(1);
    expect(component.loadError()).toBeNull();
    expect(notify).toHaveBeenCalledOnce();
  });
  it('locks toggle before confirmation and unlocks after cancellation', async () => {
    let answer!: (value: boolean) => void;
    const confirm = vi
      .spyOn(TestBed.inject(ConfirmService), 'open')
      .mockReturnValue(new Promise<boolean>((resolve) => (answer = resolve)));
    const pending = component.onToggleActive(tenant);
    const duplicate = component.onToggleActive(tenant);
    fixture.detectChanges();
    try {
      expect(confirm).toHaveBeenCalledOnce();
      expect(component.activationId()).toBeTruthy();
      expect(table().activationId()).toBe(component.activationId());
    } finally {
      answer(false);
      await Promise.all([pending, duplicate]);
    }
    expect(component.activationId()).toBeFalsy();
  });

  it('locks delete before confirmation and unlocks after cancellation', async () => {
    let answer!: (value: boolean) => void;
    const confirm = vi
      .spyOn(TestBed.inject(ConfirmService), 'open')
      .mockReturnValue(new Promise<boolean>((resolve) => (answer = resolve)));
    const pending = component.onDelete(tenant);
    const duplicate = component.onDelete(tenant);
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
  //#if (Impersonation)

  it('locks impersonate before confirmation and unlocks after cancellation', async () => {
    let answer!: (value: boolean) => void;
    const confirm = vi
      .spyOn(TestBed.inject(ConfirmService), 'open')
      .mockReturnValue(new Promise<boolean>((resolve) => (answer = resolve)));
    const pending = component.onImpersonate(tenant);
    const duplicate = component.onImpersonate(tenant);
    fixture.detectChanges();
    try {
      expect(confirm).toHaveBeenCalledOnce();
      expect(component.impersonatingId()).toBeTruthy();
      expect(table().impersonatingId()).toBe(component.impersonatingId());
    } finally {
      answer(false);
      await Promise.all([pending, duplicate]);
    }
    expect(component.impersonatingId()).toBeFalsy();
  });
  //#endif
});
