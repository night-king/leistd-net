// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  model,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormField, form, maxLength, required, validate } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSeparator } from '@spartan-ng/helm/separator';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { catchError, EMPTY, finalize, Subscription, tap } from 'rxjs';

import { applicationErrorMessage } from '../../../../../../core/errors/application-http-error';
import { ConfirmService } from '../../../../../../core/feedback/confirm-service';
import { SettingContextService } from '../../../../../../core/settings/setting-context-service';
import { AppDate } from '../../../../../../shared/pipes/app-date-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import {
  TENANT_CONNECTION_NAME_PATTERN,
  TENANT_CONNECTION_STRING_MAX_LENGTH,
  TenantConnectionDto,
  normalizeTenantConnectionName,
} from '../../../../dtos/tenant-connection.dto';
import { TenantOutputDto } from '../../../../dtos/tenant.dto';
import { TenantConnectionService } from '../../../../services/tenant-connection-service';

/** 连接编辑器的三档：收起 / 添加一条 / 改某条的连接串。 */
type ConnectionEditorMode = 'idle' | 'add' | 'edit';

interface ConnectionEditorModel {
  name: string;
  connectionString: string;
}

/**
 * 租户详情。标识来自列表已有的租户对象；数据库连接另行请求，失败只让连接段降级。
 *
 * 连接是一张表：每个服务可登记一条，名字是使用方 DbContext 的连接名；一条都没有即不单独分库，
 * 空列表须显式说明，否则与漏登记无法区分。连接串只写，从不预填或读回。{@link canManage} 为假时
 * 不发连接请求（需要 `App.Tenants.Update`），也不渲染连接段与编辑按钮。
 */
@Component({
  selector: 'app-tenant-detail-dialog',
  // prettier-ignore
  imports: [
    AppDate,
    FormField,
    HlmButton,
    HlmInput,
    HlmSeparator,
    HlmSpinner,
    ...HlmDialogImports,
    ...HlmFieldImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './tenant-detail-dialog.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TenantDetailDialog {
  readonly open = model(false);
  readonly tenant = input<TenantOutputDto | null>(null);

  /** 是否有租户更新权限：决定连接段与编辑按钮是否出现。 */
  readonly canManage = input(false);

  /**
   * 请求编辑当前租户。用 `output` 而非 `model`：连续两次编辑同一租户时，`set` 同一引用
   * 不会触发变化，编辑框不会打开。
   */
  readonly edit = output<TenantOutputDto>();

  private readonly connectionService = inject(TenantConnectionService);
  private readonly confirmService = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly displayTimeZone = inject(SettingContextService).timeZone;
  protected readonly displayLocale = inject(SettingContextService).displayLocale;
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** `null` = 尚未拿到列表；空数组 = 拿到了，该租户不单独分库。两者在界面上不是一回事。 */
  protected readonly connections = signal<TenantConnectionDto[] | null>(null);

  /**
   * 能否现在登记连接，照搬后端判据：此前没有任何登记时，新登记会把租户从"不分库"改成"分库"，
   * 现有数据不会搬过去，后端对在用租户以 409 拒绝；已分库的租户补登新名字不受限。
   */
  protected readonly canAddConnection = computed(() => {
    const list = this.connections();
    if (list === null) {
      return false;
    }

    return list.length > 0 || this.tenant()?.isActive !== true;
  });
  protected readonly connectionsLoading = signal(false);
  protected readonly connectionsError = signal<string | null>(null);

  /** 写操作的失败面与列表查询分开：一条改不动，不该把已经列出来的连接一起清掉。 */
  protected readonly actionError = signal<string | null>(null);
  protected readonly submitting = signal(false);

  protected readonly editorMode = signal<ConnectionEditorMode>('idle');
  /** 'edit' 档下正在改的那条；决定提交时用哪个名字与哪个版本。 */
  private readonly editingConnection = signal<TenantConnectionDto | null>(null);

  /** 刷新令牌：写操作成功后递增，复用加载 effect 那条带取消语义的路径重新拉列表。 */
  private readonly reloadToken = signal(0);

  protected readonly editorModel = signal<ConnectionEditorModel>({
    name: '',
    connectionString: '',
  });

  readonly connectionForm = form(this.editorModel, (path) => {
    required(path.name, { when: () => this.editorMode() === 'add' });
    validate(path.name, (ctx) => this.validateName(ctx.value()));
    required(path.connectionString);
    maxLength(path.connectionString, TENANT_CONNECTION_STRING_MAX_LENGTH);
  });

  constructor() {
    effect((onCleanup) => {
      const tenant = this.tenant();
      // 读一次刷新令牌，把"写完重新拉一遍"也并到这条路径上
      this.reloadToken();

      if (!this.open() || !tenant || !this.canManage()) {
        return;
      }

      const subscription = this.loadConnections(tenant.id);

      // 关闭或换租户时取消上一次查询，否则 A 的慢响应会配到 B 的标识上。
      onCleanup(() => subscription.unsubscribe());
    });

    // 换租户或开关弹窗时收起编辑器：留着它，下次打开就会把上一个租户的连接名
    // 摆在表单里，还可能带着刚才敲进去一半的连接串。
    effect(() => {
      this.tenant();
      this.open();
      this.closeEditor();
    });
  }

  private loadConnections(tenantId: string): Subscription {
    this.connections.set(null);
    this.connectionsError.set(null);
    this.connectionsLoading.set(true);

    return this.connectionService
      .getConnections(tenantId)
      .pipe(
        tap((connections) => this.connections.set(connections)),
        catchError((error: unknown) => {
          this.connectionsError.set(applicationErrorMessage(error));
          return EMPTY;
        }),
        // 取消订阅时也会走到这里，因此关掉弹窗不会把加载态留在 true
        finalize(() => this.connectionsLoading.set(false)),
      )
      .subscribe();
  }

  onEdit(): void {
    const tenant = this.tenant();
    if (!tenant) {
      return;
    }

    this.open.set(false);
    this.edit.emit(tenant);
  }

  startAdd(): void {
    this.actionError.set(null);
    this.editingConnection.set(null);
    this.editorModel.set({ name: '', connectionString: '' });
    this.editorMode.set('add');
  }

  startEdit(connection: TenantConnectionDto): void {
    this.actionError.set(null);
    this.editingConnection.set(connection);
    // 连接串不预填：接口从不返回它，填一个占位值只会被原样提交回去覆盖真值
    this.editorModel.set({ name: connection.name, connectionString: '' });
    this.editorMode.set('edit');
  }

  closeEditor(): void {
    this.editorMode.set('idle');
    this.editingConnection.set(null);
    // 连接串不留在内存里等下一次打开
    this.editorModel.set({ name: '', connectionString: '' });
  }

  /** 提交登记或改连接串。`PUT` 整串覆盖，没有"不传即保留"，因此连接串留空按必填报错。 */
  submitEditor(): void {
    const tenant = this.tenant();
    if (!tenant || this.editorMode() === 'idle' || this.connectionForm().invalid()) {
      return;
    }

    const editing = this.editingConnection();
    const model = this.editorModel();
    // 名字大小写不敏感：先归一化再提交，免得界面上看着是新的一条、写进去是同一行
    const name = editing ? editing.name : normalizeTenantConnectionName(model.name);
    // 首次登记预期这一条尚不存在（null）；改已有的那条必须带上读到的版本，
    // 否则后端按"忘了带版本"拒绝，而不是让后写者悄悄盖掉别人的改动。
    const expectedVersion = editing ? editing.version : null;

    this.actionError.set(null);
    this.submitting.set(true);
    this.connectionService
      .setConnection(tenant.id, name, {
        expectedVersion,
        connectionString: model.connectionString,
      })
      .pipe(
        finalize(() => this.submitting.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.closeEditor();
          this.reload();
        },
        // 失败时留着编辑器不关：连接串是手敲进来的，关掉等于让人重敲一遍
        error: (error: unknown) => this.actionError.set(applicationErrorMessage(error)),
      });
  }

  async removeConnection(connection: TenantConnectionDto): Promise<void> {
    const tenant = this.tenant();
    if (!tenant) {
      return;
    }

    const confirmed = await this.confirmService.open({
      //#if (IncludeLocalization)
      header: this.transloco.translate('tenants.deleteConnectionTitle'),
      message: this.transloco.translate('tenants.deleteConnectionDescription', {
        name: connection.name,
      }),
      confirmText: this.transloco.translate('common.delete'),
      //#else
      header: 'Delete connection',
      message: `Delete connection "${connection.name}"? That service will go back to the database it is configured with.`,
      confirmText: 'Delete',
      //#endif
      variant: 'destructive',
    });

    if (!confirmed) {
      return;
    }

    this.actionError.set(null);
    this.connectionService
      .removeConnection(tenant.id, connection.name, connection.version)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.reload(),
        error: (error: unknown) => this.actionError.set(applicationErrorMessage(error)),
      });
  }

  private reload(): void {
    this.reloadToken.update((token) => token + 1);
  }

  /** 名字只在"添加"档校验：改连接串时名字来自已登记的那条，本就合法。 */
  private validateName(value: string): { kind: string } | null {
    if (this.editorMode() !== 'add') {
      return null;
    }

    const name = normalizeTenantConnectionName(value);
    if (!name) {
      // 空值归 required 报，这里不重复提示
      return null;
    }

    if (!TENANT_CONNECTION_NAME_PATTERN.test(name)) {
      return { kind: 'connectionNamePattern' };
    }

    // 同名再"添加"一次会被后端按版本冲突拒掉，在这里就说清楚该走"改连接串"
    if (this.connections()?.some((connection) => connection.name === name)) {
      return { kind: 'connectionNameTaken' };
    }

    return null;
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'tenants.detailTitle': 'Tenant detail',
  'tenants.detailSectionIdentity': 'Identity',
  'tenants.fieldName': 'Name',
  'tenants.fieldDisplayName': 'Display name',
  'tenants.fieldDescription': 'Description',
  'tenants.colStatus': 'Status',
  'tenants.active': 'Active',
  'tenants.inactive': 'Inactive',
  'tenants.colCreatedAt': 'Created at',
  'tenants.detailSectionConnections': 'Database connections',
  'tenants.addConnection': 'Add connection',
  'tenants.shardRequiresInactive':
    'To give this tenant a database of its own, deactivate it first: its data currently lives in the database each service is configured with, and registering a connection does not move it.',
  'tenants.fieldConnectionVersion': 'Version',
  'tenants.changeConnectionString': 'Change connection string',
  'common.delete': 'Delete',
  'tenants.connectionsEmpty':
    'This tenant has no database of its own; every service uses the database it is configured with.',
  'tenants.fieldConnectionName': 'Connection name',
  'tenants.fieldConnectionString': 'Connection string',
  'tenants.detailSecretHint':
    'The connection string is stored encrypted and never returned; saving replaces it in full.',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
  'common.edit': 'Edit',
  'common.close': 'Close',
};
//#endif
