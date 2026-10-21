import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, Subject } from 'rxjs';

import { TenantDetailDialog } from './tenant-detail-dialog';
import { ConfirmService } from '../../../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { TenantConnectionDto } from '../../../../dtos/tenant-connection.dto';
import { TenantOutputDto } from '../../../../dtos/tenant.dto';
import { TenantConnectionService } from '../../../../services/tenant-connection-service';

import type { Mock, MockedObject } from 'vitest';

/**
 * 连接段的用例钉住：换租户或关闭时取消慢查询、空列表显式说明不分库、`expectedVersion`
 * 首次登记传 null 而修改传读到的版本、连接串从不预填。断言落在渲染结果与请求参数上。
 */
describe('TenantDetailDialog', () => {
  let fixture: ComponentFixture<TenantDetailDialog>;
  let component: TenantDetailDialog;
  let pending: Map<string, Subject<TenantConnectionDto[]>>;
  let requested: string[];
  let setConnection: Mock;
  let removeConnection: Mock;
  let confirm: Pick<MockedObject<ConfirmService>, 'open'>;

  function tenant(id: string, name: string): TenantOutputDto {
    return {
      id,
      name,
      displayName: name.toUpperCase(),
      isActive: true,
      creationTime: '2026-01-15T12:00:00Z',
    };
  }

  function connectionOf(tenantId: string, name: string, version = 1): TenantConnectionDto {
    return { tenantId, name, version };
  }

  async function setUp(): Promise<void> {
    pending = new Map();
    requested = [];
    setConnection = vi
      .fn()
      .mockName('setConnection')
      .mockReturnValue(of(connectionOf('t-a', 'crm')));
    removeConnection = vi.fn().mockName('removeConnection').mockReturnValue(of(undefined));

    confirm = {
      open: vi.fn().mockName('ConfirmService.open'),
    };
    confirm.open.mockResolvedValue(true);

    TestBed.configureTestingModule({
      imports: [TenantDetailDialog],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: ConfirmService, useValue: confirm },
        {
          provide: TenantConnectionService,
          useValue: {
            // 每个租户一个 Subject：用例自己决定谁先返回，才能造出乱序
            getConnections: (id: string): Observable<TenantConnectionDto[]> => {
              requested.push(id);
              const subject = new Subject<TenantConnectionDto[]>();
              pending.set(id, subject);
              return subject;
            },
            setConnection,
            removeConnection,
          },
        },
      ],
    });

    fixture = TestBed.createComponent(TenantDetailDialog);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('canManage', true);
    await fixture.whenStable();
  }

  async function open(target: TenantOutputDto): Promise<void> {
    fixture.componentRef.setInput('tenant', target);
    fixture.componentRef.setInput('open', true);
    await fixture.whenStable();
  }

  async function close(): Promise<void> {
    fixture.componentRef.setInput('open', false);
    await fixture.whenStable();
  }

  // 弹窗内容经 *hlmDialogPortal 渲染到 CDK 浮层里，不在组件宿主元素内——
  // 断言得看 document，否则永远拿到空串（那会让所有断言都"通过"成假的）。
  const dialogHost = () => document.querySelector('[role="dialog"]') as HTMLElement | null;
  const text = () => dialogHost()?.textContent ?? '';
  /** 某个键在界面上的文案：用例词条为空时就是键名，不含本地化时取组件自带的英文表。 */
  //#if (IncludeLocalization)
  const label = (key: string) => key;
  //#else
  const label = (key: string) => component['t'](key);
  //#endif
  const connectionStringField = () =>
    document.getElementById('tenant-connection-string') as HTMLInputElement | null;

  async function resolve(tenantId: string, connections: TenantConnectionDto[]): Promise<void> {
    const subject = pending.get(tenantId);
    subject?.next(connections);
    subject?.complete();
    await fixture.whenStable();
  }

  /** 打开某个租户并把它的连接列表交付到位。 */
  async function openWith(
    target: TenantOutputDto,
    connections: TenantConnectionDto[],
  ): Promise<void> {
    await open(target);
    await resolve(target.id, connections);
  }

  it('ignores a slow response for the previous tenant after switching to another tenant', async () => {
    await setUp();
    const acme = tenant('t-a', 'acme');
    const globex = tenant('t-b', 'globex');

    await open(acme);
    await close();
    await open(globex);

    expect(requested).toEqual(['t-a', 't-b']);

    // globex 先回来，acme 迟到。发完要 complete，否则界面一直停在加载态。
    await resolve('t-b', [connectionOf('t-b', 'globex-crm')]);
    await resolve('t-a', [connectionOf('t-a', 'acme-legacy')]);

    expect(text()).toContain('globex-crm');
    // 取消掉的那次请求不该再写回界面，否则 globex 的身份信息会配上 acme 的连接列表
    expect(text()).not.toContain('acme-legacy');
  });

  // 一条登记都没有就是"不单独分库"这一档。这句话是界面上唯一能看出当前状态的东西：
  // 没有它，"故意不分库"与"漏登记"就长得一模一样。
  it('states explicitly that the tenant has no separate database when the list is empty', async () => {
    await setUp();

    await openWith(tenant('t-a', 'acme'), []);

    expect(text()).toContain(label('tenants.connectionsEmpty'));
  });

  it('lists registered connections by name and version without showing connection strings', async () => {
    await setUp();

    await openWith(tenant('t-a', 'acme'), [
      connectionOf('t-a', 'default', 3),
      connectionOf('t-a', 'crm', 1),
    ]);

    expect(text()).toContain('default');
    expect(text()).toContain('crm');
    expect(text()).toContain(label('tenants.fieldConnectionVersion'));
    expect(text()).not.toContain(label('tenants.connectionsEmpty'));
    expect(text()).not.toMatch(/Host=|Password=|User ID=/i);
  });

  it('add connection: normalizes the name to lowercase and sends a null expected version on first registration', async () => {
    await setUp();
    await openWith(tenant('t-a', 'acme'), []);

    component.startAdd();
    // 填 Crm 也放行：后端按小写归一化存取，两种写法命中同一条
    component.connectionForm.name().value.set('Crm');
    component.connectionForm.connectionString().value.set('Host=db;Password=spec');
    await fixture.whenStable();

    component.submitEditor();

    expect(setConnection).toHaveBeenCalledWith('t-a', 'crm', {
      expectedVersion: null,
      connectionString: 'Host=db;Password=spec',
    });
  });

  it('change connection string: keeps the name, sends the version read, and never prefills the input', async () => {
    await setUp();
    const existing = connectionOf('t-a', 'crm', 5);
    await openWith(tenant('t-a', 'acme'), [existing]);

    component.startEdit(existing);
    await fixture.whenStable();

    // 连接串加密存储、接口从不返回，预填一个占位值只会被原样提交回去覆盖真值
    expect(connectionStringField()?.value).toBe('');
    expect(connectionStringField()?.type).toBe('password');

    component.connectionForm.connectionString().value.set('Host=db;Password=rotated');
    await fixture.whenStable();
    component.submitEditor();

    // 版本传错（比如照搬 null）后端会按"忘了带版本"拒绝，或者让后写者盖掉别人的改动
    expect(setConnection).toHaveBeenCalledWith('t-a', 'crm', {
      expectedVersion: 5,
      connectionString: 'Host=db;Password=rotated',
    });
  });

  // PUT 是整串覆盖，后端没有"不传即保留原值"这一档：留空静默不提交会让人以为已经保存
  it('marks the form invalid and does not submit when the connection string is empty', async () => {
    await setUp();
    const existing = connectionOf('t-a', 'crm', 5);
    await openWith(tenant('t-a', 'acme'), [existing]);

    component.startEdit(existing);
    await fixture.whenStable();

    expect(component.connectionForm().invalid()).toBe(true);
    component.submitEditor();

    expect(setConnection).not.toHaveBeenCalled();
  });

  it('marks the form invalid and does not submit when the connection name is malformed', async () => {
    await setUp();
    await openWith(tenant('t-a', 'acme'), []);

    component.startAdd();
    component.connectionForm.name().value.set('crm db!');
    component.connectionForm.connectionString().value.set('Host=db;Password=spec');
    await fixture.whenStable();

    expect(component.connectionForm().invalid()).toBe(true);
    expect(
      component.connectionForm
        .name()
        .errors()
        .map((error) => error.kind),
    ).toContain('connectionNamePattern');
    component.submitEditor();

    expect(setConnection).not.toHaveBeenCalled();
  });

  // 同名再"添加"一次会被后端按版本冲突拒掉，在提交前就说清楚该走"改连接串"
  it('marks the form invalid and does not submit when the connection name is already registered', async () => {
    await setUp();
    await openWith(tenant('t-a', 'acme'), [connectionOf('t-a', 'crm', 2)]);

    component.startAdd();
    component.connectionForm.name().value.set('CRM');
    component.connectionForm.connectionString().value.set('Host=db;Password=spec');
    await fixture.whenStable();

    expect(component.connectionForm().invalid()).toBe(true);
    expect(
      component.connectionForm
        .name()
        .errors()
        .map((error) => error.kind),
    ).toContain('connectionNameTaken');
    component.submitEditor();

    expect(setConnection).not.toHaveBeenCalled();
  });

  it('confirms before deleting a connection, then sends the version read', async () => {
    await setUp();
    const existing = connectionOf('t-a', 'crm', 7);
    await openWith(tenant('t-a', 'acme'), [existing]);

    await component.removeConnection(existing);

    expect(confirm.open).toHaveBeenCalled();
    expect(removeConnection).toHaveBeenCalledWith('t-a', 'crm', 7);
  });

  it('does not send a request when the delete confirmation is cancelled', async () => {
    await setUp();
    const existing = connectionOf('t-a', 'crm', 7);
    await openWith(tenant('t-a', 'acme'), [existing]);
    confirm.open.mockResolvedValue(false);

    await component.removeConnection(existing);

    expect(removeConnection).not.toHaveBeenCalled();
  });

  it('cancels the in-flight connection query on close without leaving the loading state behind', async () => {
    await setUp();
    await open(tenant('t-a', 'acme'));

    const subject = pending.get('t-a')!;
    expect(subject.observed).toBe(true);

    await close();

    expect(subject.observed).toBe(false);
  });

  // 连续两次编辑同一个租户：第二次也必须发出事件。
  // 用 model 表达这个动作时，第二次 set 拿到同一个对象引用，signal 判等后静默不发。
  it('emits the event on each of two consecutive edit clicks for the same tenant', async () => {
    await setUp();
    const acme = tenant('t-a', 'acme');
    const seen: TenantOutputDto[] = [];
    component.edit.subscribe((value) => seen.push(value));

    await open(acme);
    component.onEdit();
    await fixture.whenStable();

    await open(acme);
    component.onEdit();
    await fixture.whenStable();

    expect(seen.length).toBe(2);
    expect(seen[1]).toBe(acme);
  });

  // 连接接口要求 App.Tenants.Update：没有这个权限就别发那些必然 403 的请求，
  // 也别渲染一个点不动的编辑按钮。
  it('does not request the connection list or render the edit button without update permission', async () => {
    await setUp();
    fixture.componentRef.setInput('canManage', false);
    await open(tenant('t-a', 'acme'));

    expect(requested).toEqual([]);
    // 连接段与编辑按钮都不该出现（弹窗自带的关闭按钮不算）
    expect(text()).not.toContain(label('tenants.detailSectionConnections'));
    const labels = [...(dialogHost()?.querySelectorAll('button') ?? [])].map((button) =>
      (button.textContent ?? '').trim(),
    );
    expect(labels).not.toContain(label('common.edit'));
    expect(labels).toContain(label('common.close'));
  });
});
