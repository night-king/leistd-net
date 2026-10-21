import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif

import { TenantEditDialog } from './tenant-edit-dialog';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import {
  CreateTenantInputDto,
  TenantOutputDto,
  UpdateTenantInputDto,
} from '../../../../dtos/tenant.dto';

/**
 * 新建与编辑共用对话框：新建连带初始管理员，编辑只改名称与显示名。分流靠 `isEdit()`，
 * 漏掉一处编译与 lint 都看不出。
 */
@Component({
  imports: [TenantEditDialog],
  template: `
    <app-tenant-edit-dialog [(open)]="open" [tenant]="tenant()" (save)="saved.push($event)" />
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly tenant = signal<TenantOutputDto | null>(null);
  readonly saved: (CreateTenantInputDto | UpdateTenantInputDto)[] = [];
}

describe('TenantEditDialog', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  const existing: TenantOutputDto = {
    id: '019ff8ed-221b-7673-9ba8-6b6dd5a638ab',
    name: 'acme',
    displayName: 'Acme Inc.',
    isActive: true,
    creationTime: '2026-08-14T00:00:00Z',
  };

  /** 对话框实例：表单值只能从它的 tenantForm 写入，才能覆盖真实的校验规则。 */
  function dialog(): TenantEditDialog {
    return fixture.debugElement.query(By.directive(TenantEditDialog))
      .componentInstance as TenantEditDialog;
  }

  /** 切到编辑模式：租户变化会重新触发打开时的表单回填。 */
  async function switchToEdit(): Promise<void> {
    host.tenant.set(existing);
    await fixture.whenStable();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      // prettier-ignore
      providers: [
                provideZonelessChangeDetection(),
                //#if (IncludeLocalization)
                ...provideTranslocoTesting(['en']),
                //#endif
            ],
    });

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  it('rejects the create form without an admin email or initial password', async () => {
    dialog().tenantForm.name().value.set('acme');
    await fixture.whenStable();

    // 只填名称就能提交的话，后端会收到一个没有管理员的租户——谁都进不去。
    expect(dialog().tenantForm().invalid()).toBe(true);
    dialog().onSubmit();
    expect(host.saved).toEqual([]);

    // 密码强度规则同样只在新建模式生效，弱密码不能放行。
    dialog().tenantForm.adminEmail().value.set('admin@example.test');
    dialog().tenantForm.adminPassword().value.set('weak');
    await fixture.whenStable();

    expect(dialog().tenantForm().invalid()).toBe(true);
    dialog().onSubmit();
    expect(host.saved).toEqual([]);
  });

  /** 名称字段下显示出来的校验提示；对话框渲染在 document 上的浮层里。 */
  function shownNameErrors(): string[] {
    const field = document.getElementById('tenant-name')!.closest('hlm-field')!;
    return Array.from(field.querySelectorAll('hlm-field-error'))
      .map((element) => element.textContent!.trim())
      .filter((text) => text.length > 0);
  }

  function nameErrorKinds(): string[] {
    return dialog()
      .tenantForm.name()
      .errors()
      .map((error) => error.kind);
  }

  // 租户名按单个 DNS 标签校验：配置子域名解析时名字就是主机名的一段，不合规的名字建得出来却访问不到
  it.each(['Invalid Name!', '-acme', 'acme-', 'acme_1', 'acme.example', '租户'])(
    'rejects %j as a tenant name and does not submit',
    async (name) => {
      dialog().tenantForm.name().value.set(name);
      dialog().tenantForm.adminEmail().value.set('admin@example.test');
      dialog().tenantForm.adminPassword().value.set('TenantSpec!Pw1');
      await fixture.whenStable();

      expect(nameErrorKinds()).toEqual(['tenantNamePattern']);
      dialog().onSubmit();
      expect(host.saved).toEqual([]);
    },
  );

  // 首尾空白提交前会去掉，按去掉之后的值校验；超长只报一条长度提示
  it('accepts DNS labels, ignores surrounding blanks and reports an overlong name once', async () => {
    for (const name of ['Acme-2', '  acme  ', 'a', 'a'.repeat(63)]) {
      dialog().tenantForm.name().value.set(name);
      await fixture.whenStable();
      expect(nameErrorKinds()).toEqual([]);
    }

    dialog().tenantForm.name().value.set('a'.repeat(64));
    await fixture.whenStable();
    expect(nameErrorKinds()).toEqual(['maxLength']);
  });

  //#if (IncludeLocalization)
  it('shows the tenant name rule from the validation.tenantNamePattern entry', async () => {
    TestBed.inject(TranslocoService).setTranslation(
      { validation: { tenantNamePattern: 'Letters, digits and hyphens only' } },
      'en',
    );
    dialog().tenantForm.name().value.set('Invalid Name!');
    dialog().tenantForm.name().markAsTouched();
    await fixture.whenStable();

    expect(shownNameErrors()).toEqual(['Letters, digits and hyphens only']);
  });
  //#else
  it('shows the tenant name rule from the built-in English table', async () => {
    dialog().tenantForm.name().value.set('Invalid Name!');
    dialog().tenantForm.name().markAsTouched();
    await fixture.whenStable();

    expect(shownNameErrors()).toEqual([
      'Use letters, digits and hyphens only, not starting or ending with a hyphen, up to 63 characters.',
    ]);
  });
  //#endif

  it('hides the admin account fields in edit mode and passes with the name alone', async () => {
    expect(document.getElementById('tenant-admin-email')).not.toBeNull();
    expect(document.getElementById('tenant-admin-password')).not.toBeNull();

    await switchToEdit();

    // 字段不渲染却仍参与校验，保存按钮会被两个看不见的空字段永久禁用。
    expect(document.getElementById('tenant-admin-email')).toBeNull();
    expect(document.getElementById('tenant-admin-password')).toBeNull();
    expect(dialog().isEdit()).toBe(true);
    expect(dialog().tenantForm().invalid()).toBe(false);
    expect(dialog().tenantForm.name().value()).toBe('acme');
    expect(dialog().tenantForm.displayName().value()).toBe('Acme Inc.');
  });

  it('lets an existing tenant with a pre-rule name be edited, but checks a changed name', async () => {
    host.tenant.set({ ...existing, name: 'acme_corp' });
    await fixture.whenStable();

    // 存量名称不合规、没改名：只改显示名照常可保存
    dialog().tenantForm.displayName().value.set('Acme Corp');
    await fixture.whenStable();
    expect(dialog().tenantForm.name().errors()).toEqual([]);

    // 改成另一个不合规的名称就按规则拦下
    dialog().tenantForm.name().value.set('acme corp');
    await fixture.whenStable();
    expect(
      dialog()
        .tenantForm.name()
        .errors()
        .map((error) => error.kind),
    ).toEqual(['tenantNamePattern']);
  });

  // 规则之前的名字可能恰为 64 个字符、或带首尾空白：不改名时既不按新规则拦，也原样送回（trim 会变成改名）
  it('keeps a pre-rule name exactly as it is when only other fields change', async () => {
    for (const legacyName of [`${'a'.repeat(62)}_x`, ' acme_corp ']) {
      host.saved.length = 0;
      host.tenant.set({ ...existing, name: legacyName });
      await fixture.whenStable();

      dialog().tenantForm.displayName().value.set('Legacy Inc.');
      await fixture.whenStable();
      expect(dialog().tenantForm.name().errors()).toEqual([]);

      dialog().onSubmit();
      expect((host.saved[0] as UpdateTenantInputDto).name).toBe(legacyName);
    }
  });

  it('sends the admin on create, trims the name and omits a blank display name', async () => {
    dialog().tenantForm.name().value.set('  acme  ');
    dialog().tenantForm.displayName().value.set('   ');
    dialog().tenantForm.adminEmail().value.set('admin@example.test');
    // 尾部空格：密码里的空白是密码的一部分，被 trim 掉用户就再也登不进去。
    dialog().tenantForm.adminPassword().value.set('TenantSpec!Pw1 ');
    await fixture.whenStable();

    dialog().onSubmit();

    const dto = host.saved[0] as CreateTenantInputDto;
    expect(dto.name).toBe('acme');
    // 显示名可选：空字符串会被存成一个空的显示名，比不填更难改回来。
    expect(dto.displayName).toBeUndefined();
    expect(dto.adminEmail).toBe('admin@example.test');
    expect(dto.adminPassword).toBe('TenantSpec!Pw1 ');
  });

  // 分库只在新建时定案：登记先于播种，种子才落进那个库；建好后再登记第一条连接会被 409 拒绝。
  it('includes the connection string in the create payload', async () => {
    dialog().tenantForm.name().value.set('acme');
    dialog().tenantForm.adminEmail().value.set('admin@example.test');
    dialog().tenantForm.adminPassword().value.set('TenantSpec!Pw1');
    dialog().tenantForm.connectionString().value.set('  Host=acme;Database=acme  ');
    await fixture.whenStable();

    dialog().onSubmit();

    expect(Object.keys(host.saved[0]).sort()).toEqual([
      'adminEmail',
      'adminPassword',
      'connections',
      'description',
      'displayName',
      'name',
    ]);
    // 界面只收默认库一条，契约本身是命名连接数组
    expect((host.saved[0] as CreateTenantInputDto).connections).toEqual([
      { name: 'default', connectionString: 'Host=acme;Database=acme' },
    ]);
  });

  // 留空必须是空数组而不是一条空连接串：后端对"给了连接但连接串是空的"按 400 拒绝，
  // 而这里表达的是"不分库"，两者不能长成同一个请求。
  it('sends no connections for a blank connection string (no dedicated database)', async () => {
    dialog().tenantForm.name().value.set('acme');
    dialog().tenantForm.adminEmail().value.set('admin@example.test');
    dialog().tenantForm.adminPassword().value.set('TenantSpec!Pw1');
    await fixture.whenStable();

    dialog().onSubmit();

    expect((host.saved[0] as CreateTenantInputDto).connections).toEqual([]);
  });

  it('submits only the tenant identity fields on edit, without admin fields', async () => {
    await switchToEdit();

    dialog().tenantForm.name().value.set('acme-renamed');
    await fixture.whenStable();

    dialog().onSubmit();

    // 编辑载荷里出现 adminEmail/adminPassword，等于用一组空凭据覆盖租户管理员。
    expect(Object.keys(host.saved[0]).sort()).toEqual(['description', 'displayName', 'name']);
    expect(host.saved[0]).toEqual({
      name: 'acme-renamed',
      displayName: 'Acme Inc.',
      // 未填描述时必须是显式 null 而不是缺字段：缺字段会被后端当成"未提供"，
      // 于是"清空描述"永远保存不下去。
      description: null,
    });
  });

  it('includes the edited description in the update payload', async () => {
    await switchToEdit();

    dialog().tenantForm.description().value.set('华东区自营资金账户');
    await fixture.whenStable();

    dialog().onSubmit();

    expect(host.saved[0]).toEqual(expect.objectContaining({ description: '华东区自营资金账户' }));
  });
});
