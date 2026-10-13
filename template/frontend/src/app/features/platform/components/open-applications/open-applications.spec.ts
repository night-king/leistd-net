import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { toast } from '@spartan-ng/brain/sonner';
import { PaginationState, SortingState } from '@tanstack/angular-table';
import { of, Subject, throwError } from 'rxjs';

import { OpenApplications } from './open-applications';
import { OpenApplicationTable } from './widgets/open-application-table/open-application-table';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { StartupService } from '../../../../core/services/startup-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
import { GetOpenApplicationsInputDto } from '../../dtos/open-application.dto';
import { OpenApplicationService } from '../../services/open-application-service';

import type { MockedObject } from 'vitest';

/** 开放应用页面的查询闭环。 */
describe('OpenApplications page query round trip', () => {
  let fixture: ComponentFixture<OpenApplications>;
  let component: OpenApplications;
  let router: Router;
  let service: Pick<
    MockedObject<OpenApplicationService>,
    'getOpenApplications' | 'getScopes' | 'resetSecret'
  >;

  function lastQuery(): GetOpenApplicationsInputDto {
    const calls = vi.mocked(service.getOpenApplications).mock.calls;
    const query = calls.at(-1)?.[0];
    if (!query) {
      throw new Error('The list request was never sent');
    }

    return query;
  }

  /** 真实的子表实例：事件必须从它的 output 发出，才能覆盖模板里的绑定名。 */
  function table(): OpenApplicationTable {
    return fixture.debugElement.query(By.directive(OpenApplicationTable))
      .componentInstance as OpenApplicationTable;
  }

  beforeEach(async () => {
    service = {
      getOpenApplications: vi.fn().mockName('OpenApplicationService.getOpenApplications'),
      getScopes: vi.fn().mockName('OpenApplicationService.getScopes'),
      resetSecret: vi.fn().mockName('OpenApplicationService.resetSecret'),
    };
    service.getOpenApplications.mockReturnValue(of({ items: [], totalCount: 0 }) as never);
    service.getScopes.mockReturnValue(of([]));

    await TestBed.configureTestingModule({
      imports: [OpenApplications],
      providers: [
        provideRouter([{ path: 'platform/open-applications', children: [] }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: OpenApplicationService, useValue: service },
        { provide: StartupService, useValue: { status: signal('success' as const) } },
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    await router.navigate(['/platform/open-applications']);

    TestBed.inject(AuthorizationService).setPermissions({
      permissions: [PERMISSIONS.openApplications.default],
      isSuperAdmin: false,
      versionToken: 'r1',
    });

    // 先建好设置上下文（连带语言服务），否则首帧渲染途中创建它会让结构指令重入。
    TestBed.inject(SettingContextService);
    fixture = TestBed.createComponent(OpenApplications);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('writes the page to the URL and refetches for the new page', async () => {
    table().paginationChange.emit({ pageIndex: 2, pageSize: 20 } as PaginationState);
    await fixture.whenStable();

    // 页码在 URL 里是 1 基（可分享、可前进后退），发给接口的是 offset。
    expect(router.url).toContain('page=3');
    expect(lastQuery().offset).toBe(40);
    expect(lastQuery().limit).toBe(20);
  });

  it('resets to the first page and offset 0 when the page size changes', async () => {
    table().paginationChange.emit({ pageIndex: 3, pageSize: 20 } as PaginationState);
    await fixture.whenStable();

    table().paginationChange.emit({ pageIndex: 0, pageSize: 50 } as PaginationState);
    await fixture.whenStable();

    expect(lastQuery().offset).toBe(0);
    expect(lastQuery().limit).toBe(50);
  });

  it('writes sorting to the URL, resets to page 1 and maps it to the API param', async () => {
    table().paginationChange.emit({ pageIndex: 2, pageSize: 20 } as PaginationState);
    await fixture.whenStable();

    table().sortingChange.emit([{ id: 'displayName', desc: true }] as SortingState);
    await fixture.whenStable();

    // 停在第 3 页换排序，看到的是另一批数据的第 3 页，等于结果错乱。
    expect(router.url).toContain('page=1');
    expect(lastQuery().offset).toBe(0);
    expect(lastQuery().sorting).toBeTruthy();
  });

  it('drops the previous secret after the secret dialog closes', () => {
    // 关闭弹窗只更新 visible 时，secret 与所属应用会留在组件状态里，
    // 下一次误打开弹窗会显示上一次生成的 secret。
    component.secretValue.set('generated-secret');
    component.secretApplication.set({ clientId: 'demo-client' });
    component.secretDialogVisible.set(true);

    component.onSecretDialogVisibleChange(false);

    expect(component.secretDialogVisible()).toBe(false);
    expect(component.secretValue()).toBe('');
    expect(component.secretApplication()).toBeNull();
  });

  it('keeps the secret while the secret dialog opens', () => {
    component.secretValue.set('generated-secret');
    component.secretApplication.set({ clientId: 'demo-client' });

    component.onSecretDialogVisibleChange(true);

    expect(component.secretDialogVisible()).toBe(true);
    expect(component.secretValue()).toBe('generated-secret');
  });

  it('shows the reset secret together with the application it belongs to', async () => {
    vi.spyOn(TestBed.inject(ConfirmService), 'open').mockResolvedValue(true);
    service.getOpenApplications.mockReturnValue(
      of({ items: [{ ...loadedRow, displayName: 'Demo SPA' }], totalCount: 1 }) as never,
    );
    service.resetSecret.mockReturnValue(of({ clientSecret: 'reset-secret' }));
    component.reloadList();
    await fixture.whenStable();

    await component.handleResetSecret('row-1');

    expect(component.secretDialogVisible()).toBe(true);
    expect(component.secretKind()).toBe('reset');
    expect(component.secretValue()).toBe('reset-secret');
    expect(component.secretApplication()).toEqual(
      expect.objectContaining({ clientId: 'spa', displayName: 'Demo SPA' }),
    );
  });

  it('restores component state from the URL on reload and back/forward navigation', async () => {
    await router.navigate(['/platform/open-applications'], {
      queryParams: { page: 2, pageSize: 50, keyword: 'probe' },
    });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.pagination()).toEqual(expect.objectContaining({ pageIndex: 1, pageSize: 50 }));
    expect(lastQuery().keyword).toBe('probe');
  });

  it('keeps loading on while a superseded request is cancelled and the new one is in flight', async () => {
    const first = new Subject<never>();
    const second = new Subject<{ items: never[]; totalCount: number }>();
    service.getOpenApplications
      .mockReturnValueOnce(first as never)
      .mockReturnValueOnce(second as never);

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
    clientId: 'spa',
    applicationType: 'web',
    clientType: 'public',
    redirectUris: [],
    postLogoutRedirectUris: [],
    permissions: [],
    requirements: [],
    settings: {},
    properties: {},
    hasClientSecret: false,
    sessionBound: true,
    creationTime: '2026-01-01T00:00:00Z',
  };

  /** 以首次请求失败重建页面：此时没有任何旧行可保留。 */
  async function openWithFailedFirstLoad(): Promise<void> {
    fixture.destroy();
    service.getOpenApplications.mockReturnValue(throwError(() => new Error('boom')) as never);
    fixture = TestBed.createComponent(OpenApplications);
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
    expect(component.applications()).toEqual([]);
    expect(notify).not.toHaveBeenCalled();

    const requests = service.getOpenApplications.mock.calls.length;
    service.getOpenApplications.mockReturnValue(of({ items: [loadedRow], totalCount: 1 }) as never);
    table().retry.emit();
    await fixture.whenStable();

    expect(service.getOpenApplications).toHaveBeenCalledTimes(requests + 1);
    expect(component.loadError()).toBeNull();
    expect(component.applications()).toHaveLength(1);
  });

  it('keeps the loaded rows and only notifies when a refresh fails', async () => {
    const notify = vi.spyOn(toast, 'error').mockImplementation(() => '');
    service.getOpenApplications.mockReturnValue(of({ items: [loadedRow], totalCount: 1 }) as never);
    component.reloadList();
    await fixture.whenStable();

    service.getOpenApplications.mockReturnValue(throwError(() => new Error('boom')) as never);
    component.reloadList();
    await fixture.whenStable();

    expect(component.applications()).toHaveLength(1);
    expect(component.loadError()).toBeNull();
    expect(notify).toHaveBeenCalledOnce();
  });
});
