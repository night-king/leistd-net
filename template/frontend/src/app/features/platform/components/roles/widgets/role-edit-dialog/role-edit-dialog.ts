import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  model,
  output,
  signal,
} from '@angular/core';
import { FormField, disabled, form, maxLength, pattern, required } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckboxImports } from '@spartan-ng/helm/checkbox';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';

//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import { CreateRoleInputDto, RoleOutputDto, UpdateRoleInputDto } from '../../../../dtos/role.dto';

interface RoleEditFormModel {
  name: string;
  displayName: string;
  description: string;
  sort: number;
  isDefault: boolean;
}

/** 与服务端 CreateRoleInputDto.Name 一致：长度 2~64 与 [RegularExpression] 的字符集。 */
const ROLE_NAME_PATTERN = /^[a-zA-Z0-9_]{2,64}$/;

/** 角色新建 / 编辑对话框。角色名称是稳定的业务标识，创建后不可修改。 */
@Component({
  selector: 'app-role-edit-dialog',
  // prettier-ignore
  imports: [
    FormField,
    HlmButton,
    HlmInput,
    ...HlmDialogImports,
    ...HlmFieldImports,
    ...HlmCheckboxImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './role-edit-dialog.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoleEditDialog {
  readonly open = model(false);
  readonly role = input<RoleOutputDto | null>(null);
  readonly save = output<CreateRoleInputDto | UpdateRoleInputDto>();
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly isEdit = computed(() => this.role() !== null);

  protected readonly formModel = signal<RoleEditFormModel>({
    name: '',
    displayName: '',
    description: '',
    sort: 0,
    isDefault: false,
  });

  readonly roleForm = form(this.formModel, (path) => {
    disabled(path.name, () => this.isEdit());
    // 与 CreateRoleInputDto 的规则逐条对应（长度 2~64、字母数字下划线；显示名 128；描述 512）。
    required(path.name);
    // 长度与字符集共用一句提示，合成一条规则：分开校验会把同一句话报出几遍
    pattern(path.name, ROLE_NAME_PATTERN, { error: { kind: 'roleNamePattern' } });
    required(path.displayName);
    maxLength(path.displayName, 128);
    maxLength(path.description, 512);
  });

  constructor() {
    effect(() => {
      // 打开时按当前角色重置表单：新建走空白模型，编辑回填既有值。
      if (!this.open()) {
        return;
      }

      const role = this.role();
      this.formModel.set({
        name: role?.name ?? '',
        displayName: role?.displayName ?? '',
        description: role?.description ?? '',
        sort: role?.sort ?? 0,
        isDefault: role?.isDefault ?? false,
      });
    });
  }

  onSubmit(): void {
    if (this.roleForm().invalid()) {
      return;
    }

    const model = this.formModel();
    const description = model.description.trim() || undefined;

    if (this.isEdit()) {
      this.save.emit({
        displayName: model.displayName.trim(),
        description,
        sort: model.sort,
        isDefault: model.isDefault,
      } satisfies UpdateRoleInputDto);
      return;
    }

    this.save.emit({
      name: model.name.trim(),
      displayName: model.displayName.trim(),
      description,
      sort: model.sort,
      isDefault: model.isDefault,
    } satisfies CreateRoleInputDto);
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'roles.editTitle': 'Edit role',
  'roles.createTitle': 'New role',
  'roles.editDescription': 'Name is the stable identifier and cannot be changed after creation.',
  'roles.fieldName': 'Name',
  'roles.fieldDisplayName': 'Display name',
  'roles.fieldDescription': 'Description',
  'roles.fieldIsDefault': 'Assign to new users by default',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
};
//#endif
