import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  model,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideChevronDown,
  lucideChevronRight,
  lucideCircleAlert,
  lucideSearch,
} from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmAccordionImports } from '@spartan-ng/helm/accordion';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckboxImports } from '@spartan-ng/helm/checkbox';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import {
  HlmInputGroup,
  HlmInputGroupAddon,
  HlmInputGroupInput,
} from '@spartan-ng/helm/input-group';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { combineLatest, defer, EMPTY, of, Subject } from 'rxjs';
import { catchError, filter, finalize, startWith, switchMap, tap } from 'rxjs/operators';

import { API_ERROR_CODES } from '../../../../core/errors/api-error-codes';
import {
  applicationErrorMessage,
  ApplicationHttpError,
} from '../../../../core/errors/application-http-error';
import {
  PermissionDefinitionGroupOutputDto,
  PermissionDefinitionOutputDto,
  PermissionGrantsOutputDto,
} from '../../../../shared/dtos/permission.dto';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { PermissionManagementService } from '../../services/permission-management-service';

/** 组内的一行权限；depth 0 是资源本身，更深的是可在其上执行的动作。 */
interface PermissionRow {
  name: string;
  displayName: string;
  depth: number;
  /** 顶层且带动作的行才需要标注"勾上等于能查看"——无下级的行不存在这层歧义。 */
  hasChildren: boolean;
}

interface PermissionGroupView {
  name: string;
  displayName: string;
  rows: PermissionRow[];
}

/** 渲染用分组：带过滤后的行与本组已授予数。 */
interface PermissionGroupRender extends PermissionGroupView {
  grantedCount: number;
}

/**
 * 角色权限编辑器。授予是纯加法，没有"拒绝"：收回能力应调整角色构成，减法会让有效权限不可组合。
 *
 * 分组是模块，depth 0 是资源（勾上即能查看列表），更深的是其上的动作。勾动作补齐资源、取消资源
 * 连带取消动作，与后端写入时的归一化一致，此处只是即时反馈。
 */
@Component({
  selector: 'app-permission-grant-dialog',
  imports: [
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmSpinner,
    ...HlmAccordionImports,
    HlmInputGroup,
    HlmInputGroupAddon,
    HlmInputGroupInput,
    ...HlmCheckboxImports,
    ...HlmDialogImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [
    provideIcons({ lucideSearch, lucideChevronDown, lucideChevronRight, lucideCircleAlert }),
  ],
  templateUrl: './permission-grant-dialog.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PermissionGrantDialog {
  readonly open = model(false);
  readonly roleId = input<string | null>(null);
  /** 角色展示名，仅用于标题。 */
  readonly roleName = input('');
  readonly saved = output<void>();

  private readonly permissionService = inject(PermissionManagementService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly loading = signal(false);
  /** 加载失败的原因：打开时编辑状态已清空、没有内容可保留，因此显示错误态与重试而不是空列表。 */
  readonly loadError = signal<string | null>(null);
  readonly saving = signal(false);
  readonly groups = signal<PermissionGroupView[]>([]);
  readonly version = signal(0);

  /** 已授予的权限名。 */
  private readonly granted = signal<ReadonlySet<string>>(new Set<string>());

  /**
   * 当前界面状态所属的角色，未成功加载时为 null。保存前要求与当前角色一致，
   * 免得把加载失败后"空但可编辑"的状态写成一份空授予。
   */
  private readonly loadedRoleId = signal<string | null>(null);

  /** 加载成功且归属与当前角色一致时才允许保存。 */
  readonly canSave = computed(() => {
    const loaded = this.loadedRoleId();
    return loaded !== null && loaded === this.roleId();
  });

  /** 父子关系缓存，用于自动补齐祖先与清理子孙。 */
  private ancestors: Record<string, string[]> = {};
  private descendants: Record<string, string[]> = {};

  /** 权限搜索关键字：权限多起来后没有搜索就只能靠肉眼扫，按显示名与权限名同时匹配。 */
  readonly keyword = signal('');

  readonly grantedCount = computed(() => this.granted().size);

  /** 过滤并统计后的分组；组标题显示过滤后的授予数。 */
  readonly visibleGroups = computed<PermissionGroupRender[]>(() => {
    const keyword = this.keyword().trim().toLowerCase();
    const granted = this.granted();

    return this.groups()
      .map((group) => {
        const rows = keyword
          ? group.rows.filter(
              (row) =>
                row.displayName.toLowerCase().includes(keyword) ||
                row.name.toLowerCase().includes(keyword),
            )
          : group.rows;

        return {
          ...group,
          rows,
          // 按过滤后的行统计：搜索时若仍按完整分组计数，徽标会出现 5/1 这种读不通的组合。
          grantedCount: rows.filter((row) => granted.has(row.name)).length,
        };
      })
      .filter((group) => group.rows.length > 0);
  });

  readonly hasNoMatch = computed(
    () => this.keyword().trim() !== '' && this.visibleGroups().length === 0,
  );

  onKeywordChange(value: string): void {
    this.keyword.set(value);
  }

  /**
   * 展开的分组：加载完成时给一次初值，之后归用户掌握。若按"本组已有授予"计算，
   * 取消最后一项会让整组自行折叠。
   */
  private readonly expandedGroups = signal<ReadonlySet<string>>(new Set<string>());

  isExpanded(groupName: string): boolean {
    // 搜索时一律展开：命中项藏在折叠的组里等于没搜到。
    return this.keyword().trim() !== '' || this.expandedGroups().has(groupName);
  }

  /** 回写手动展开与折叠：否则手动折叠后再搜索，isExpanded() 仍为 true 不变，手风琴不会重新打开。 */
  onGroupOpenedChange(groupName: string, opened: boolean): void {
    const next = new Set(this.expandedGroups());

    if (opened) {
      next.add(groupName);
    } else {
      next.delete(groupName);
    }

    this.expandedGroups.set(next);
  }

  /** 409 后要求重新加载；与角色变化共用同一条请求流，不另起订阅。 */
  private readonly reloadRequests = new Subject<void>();

  constructor() {
    // 单一请求流：角色切换时用 switchMap 取消上一次加载，组件销毁时随之退订。
    // 嵌套 subscribe 既不取消也不校验归属，快速切换角色时晚到的响应会落到新角色上。
    combineLatest([
      toObservable(this.open),
      toObservable(this.roleId),
      this.reloadRequests.pipe(startWith(undefined)),
    ])
      .pipe(
        filter(([open, roleId]) => open && !!roleId),
        // 先清空再加载：加载失败或响应归属不符时，界面必须停在"无角色"，
        // 而不是留着上一个角色的状态与版本被误保存。
        tap(() => this.resetState()),
        switchMap(([, roleId]) => this.loadFor(roleId as string)),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  /** 清空编辑状态并解除归属，使保存在重新加载成功前不可用。 */
  private resetState(): void {
    this.loadedRoleId.set(null);
    this.loadError.set(null);
    this.granted.set(new Set<string>());
    this.expandedGroups.set(new Set<string>());
    this.version.set(0);
  }

  /** 错误态里的重试：与 409 后的重新加载共用同一条请求流。 */
  retry(): void {
    this.reloadRequests.next();
  }

  isGranted(name: string): boolean {
    return this.granted().has(name);
  }

  /** 勾选补齐全部祖先，取消连带取消全部子孙，与后端写入时的归一化一致。 */
  onCheckedChange(name: string, checked: boolean): void {
    const next = new Set(this.granted());

    if (checked) {
      next.add(name);
      for (const ancestor of this.ancestors[name] ?? []) {
        next.add(ancestor);
      }
    } else {
      next.delete(name);
      for (const descendant of this.descendants[name] ?? []) {
        next.delete(descendant);
      }
    }

    this.granted.set(next);
  }

  onSave(): void {
    const roleId = this.roleId();
    // 未成功加载当前角色就保存，等于把界面上的残留内容写给它。
    if (!roleId || !this.canSave()) {
      return;
    }

    this.saving.set(true);
    this.permissionService
      .replaceRoleGrants(roleId, {
        expectedVersion: this.version(),
        permissionNames: [...this.granted()],
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('permissions.saved'));
          //#else
          toast.success('Permissions saved');
          //#endif
          this.saved.emit();
        },
        error: (error: unknown) => {
          this.saving.set(false);
          // 版本冲突说明另一位管理员抢先保存：重新加载，不静默覆盖。按错误码而不是 409 分支，
          // 其他同样映射为 409 的错误不该触发重新加载。
          const code = error instanceof ApplicationHttpError ? error.code : undefined;
          if (code === API_ERROR_CODES.permissionConcurrencyConflict) {
            //#if (IncludeLocalization)
            toast.error(this.transloco.translate('permissions.conflict'));
            //#else
            toast.error(
              'Someone else changed these permissions. The latest values have been reloaded.',
            );
            //#endif
            this.reloadRequests.next();
            return;
          }
          toast.error(applicationErrorMessage(error));
        },
      });
  }

  private loadFor(roleId: string) {
    // loading 必须在新一轮订阅时才置位：若在 switchMap 之前置位，switchMap 会紧接着退订上一轮，
    // 上一轮的 finalize 把 loading 打回 false，界面就在本轮还在飞的时候显示成"加载完了"。
    return defer(() => {
      this.loading.set(true);
      return this.permissionService.getDefinitions();
    }).pipe(
      tap((definitionGroups) => this.buildTree(definitionGroups)),
      switchMap(() => this.permissionService.getRoleGrants(roleId)),
      // 再核对一次归属：switchMap 已经取消了上一次订阅，这里防的是「响应内容与请求角色不符」。
      // 宁可不渲染，也不把别人的授予当成本角色的。
      filter((grants) => grants.providerKey === roleId),
      tap((grants) => this.applyGrants(grants)),
      catchError((error: unknown) => {
        this.loadError.set(applicationErrorMessage(error));
        return EMPTY;
      }),
      finalize(() => this.loading.set(false)),
      // finalize 在被 switchMap 取消时也会跑，用 of() 收尾保证流不因单次失败而终止。
      catchError(() => of(null)),
    );
  }

  private applyGrants(grants: PermissionGrantsOutputDto): void {
    this.version.set(grants.version);
    this.loadedRoleId.set(grants.providerKey);

    const granted = new Set(
      grants.grants.filter((grant) => grant.granted).map((grant) => grant.name),
    );
    this.granted.set(granted);

    // 默认展开已有授予的模块，免得一进来全是折叠的空壳；此后展开与否由用户决定。
    this.expandedGroups.set(
      new Set(
        this.groups()
          .filter((group) => group.rows.some((row) => granted.has(row.name)))
          .map((group) => group.name),
      ),
    );
  }

  private buildTree(definitionGroups: PermissionDefinitionGroupOutputDto[]): void {
    this.ancestors = {};
    this.descendants = {};

    const groups = definitionGroups.map((group) => {
      const rows: PermissionRow[] = [];
      for (const permission of group.permissions) {
        this.flatten(permission, 0, [], rows);
      }
      return { name: group.name, displayName: group.displayName, rows };
    });

    this.groups.set(groups);
  }

  /** 展平成带 depth 的行，同时留下前置关系表供勾选联动使用。 */
  private flatten(
    definition: PermissionDefinitionOutputDto,
    depth: number,
    ancestorChain: string[],
    rows: PermissionRow[],
  ): void {
    rows.push({
      name: definition.name,
      displayName: definition.displayName,
      depth,
      hasChildren: definition.children.length > 0,
    });

    this.ancestors[definition.name] = [...ancestorChain];
    this.descendants[definition.name] = [];
    for (const ancestor of ancestorChain) {
      this.descendants[ancestor].push(definition.name);
    }

    for (const child of definition.children) {
      this.flatten(child, depth + 1, [definition.name, ...ancestorChain], rows);
    }
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'permissions.dialogTitle': 'Permissions · {{name}}',
  'permissions.description':
    'Check a permission to grant it. The top-level entry of each block is its read access; granting an action grants that read access too — creating users requires viewing the user list. To take an ability away from someone, change their roles.',
  'permissions.searchPlaceholder': 'Search permissions',
  'permissions.grantedCount': '{{count}} granted',
  'common.noResults': 'No results',
  'permissions.groupSummary': '{{granted}}/{{total}}',
  'permissions.viewAccessHint': 'view list',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
  'permissions.loadFailed': "Couldn't load permissions",
  'common.retry': 'Retry',
};
//#endif
