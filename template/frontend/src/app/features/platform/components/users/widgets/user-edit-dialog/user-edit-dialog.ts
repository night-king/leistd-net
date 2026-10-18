// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  //#if (IncludeLocalization)
  inject,
  //#endif
  input,
  model,
  output,
  signal,
} from '@angular/core';
import {
  form,
  required,
  email as emailValidator,
  //#if (LocalIdentity)
  minLength,
  //#endif
  maxLength,
  pattern,
  disabled,
  FormField,
} from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
//#if (LocalIdentity)
import { lucideEye, lucideEyeOff, lucideImagePlus } from '@ng-icons/lucide';
//#else
import { lucideImagePlus } from '@ng-icons/lucide';
//#endif
import { BrnDialogState } from '@spartan-ng/brain/dialog';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
//#if (LocalIdentity)
import {
  HlmInputGroup,
  HlmInputGroupInput,
  HlmInputGroupButton,
} from '@spartan-ng/helm/input-group';
//#endif
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmSeparator } from '@spartan-ng/helm/separator';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmSwitch } from '@spartan-ng/helm/switch';

//#if (LocalIdentity)
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
} from '../../../../../../core/validation/password-rule';
//#endif
import { DialogLoading } from '../../../../../../shared/components/dialog-loading/dialog-loading';
import {
  AvatarImageRejected,
  isAvatarImageUrl,
  prepareAvatarImage,
} from '../../../../../../shared/utils/avatar-image';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import { RoleBriefDto } from '../../../../dtos/role.dto';
import {
  CreateUserInputDto,
  UpdateUserInputDto,
  UserManagementOutputDto,
} from '../../../../dtos/user-management.dto';

@Component({
  selector: 'app-user-edit-dialog',
  standalone: true,
  imports: [
    FormField,
    NgIcon,
    HlmButton,
    HlmSpinner,
    HlmInput,
    //#if (LocalIdentity)
    HlmInputGroup,
    HlmInputGroupInput,
    HlmInputGroupButton,
    //#endif
    HlmSwitch,
    HlmSeparator,
    ...HlmDialogImports,
    ...HlmFieldImports,
    ...HlmSelectImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
    DialogLoading,
  ],
  providers: [
    provideIcons({
      lucideImagePlus,
      //#if (LocalIdentity)
      lucideEye,
      lucideEyeOff,
      //#endif
    }),
  ],
  templateUrl: './user-edit-dialog.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserEditDialog {
  readonly visible = model(false);
  readonly loading = input(false);
  readonly saving = input(false);
  readonly user = input<UserManagementOutputDto | null>(null);
  readonly saved = output<CreateUserInputDto | UpdateUserInputDto>();
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  private readonly avatarMessages = () => ({
    sizeSummary: this.transloco.translate('users.editDialog.avatarTooLargeSummary'),
    sizeDetail: this.transloco.translate('users.editDialog.avatarTooLargeDetail'),
    typeSummary: this.transloco.translate('users.editDialog.avatarBadTypeSummary'),
    typeDetail: this.transloco.translate('users.editDialog.avatarBadTypeDetail'),
    decodeDetail: this.transloco.translate('users.editDialog.avatarDecodeFailed'),
  });
  //#else
  protected readonly t = englishText(ENGLISH);
  private readonly avatarMessages = () => ({
    sizeSummary: 'File too large',
    sizeDetail: 'The image cannot exceed 10 MB',
    typeSummary: 'Unsupported format',
    typeDetail: 'Please upload a PNG, JPG or WEBP image',
    decodeDetail: 'This image could not be read. Try another one.',
  });
  //#endif

  readonly avatarPreview = signal('');

  protected readonly formModel = signal({
    username: '',
    email: '',
    displayName: '',
    avatar: '',
    //#if (LocalIdentity)
    password: '',
    //#endif
    isActive: true,
    //#if (LocalIdentity)
    isEmailVerified: false,
    //#endif
    roleIds: [] as string[],
  });

  /** 空串表示两者都没填，模板显示"未命名用户"。 */
  readonly displayName = computed(
    () => this.formModel().displayName.trim() || this.formModel().username.trim(),
  );
  readonly userForm = form(this.formModel, (path) => {
    required(path.username);
    // 长度与字符集共用一句提示，合成一条规则：分开校验会把同一句话报出几遍
    pattern(path.username, /^[a-zA-Z0-9_]{3,64}$/, { error: { kind: 'usernamePattern' } });
    disabled(path.username, { when: () => this.isEditMode() });
    required(path.email);
    // 首尾空白按格式错误拒绝，因此提交时邮箱原样发出、不再 trim（后端同样先 trim）。
    emailValidator(path.email);
    maxLength(path.email, 256);
    maxLength(path.displayName, 128);
    //#if (LocalIdentity)
    // 初始密码仅在新建模式校验（编辑模式无密码字段）。
    required(path.password, { when: () => !this.isEditMode() });
    minLength(path.password, PASSWORD_MIN_LENGTH, { when: () => !this.isEditMode() });
    maxLength(path.password, PASSWORD_MAX_LENGTH, { when: () => !this.isEditMode() });
    //#endif
  });
  /**
   * 角色选项由父级从角色 API 注入，按 Id 提交、按显示名回显。
   * 仅在「新建 + 持有角色分配权限」时展示：编辑态的角色变更走独立的角色分配入口，
   * 因此只持有 Users.Update 的主体在这里看不到也提交不了角色。
   */
  readonly availableRoles = input<RoleBriefDto[]>([]);
  readonly canAssignRoles = input(false);

  readonly roleOptions = computed(() =>
    this.availableRoles().map((role) => ({ label: role.displayName, value: role.id })),
  );

  readonly showRoleField = computed(() => !this.isEditMode() && this.canAssignRoles());

  // 角色 Id → 显示名，用于 hlm-select-multiple 触发器回显。
  readonly roleLabel = (value: string): string =>
    this.roleOptions().find((option) => option.value === value)?.label ?? value;

  constructor() {
    // 同时依赖 visible 与 user：每次对话框打开都重置表单，避免新建模式残留上次输入
    // （user 信号从 null 到 null 不变化时 effect 不会重跑，需借 visible 触发）
    effect(() => {
      const user = this.user();
      if (!this.visible()) {
        return;
      }
      const avatar = user?.avatar ?? '';
      this.formModel.set({
        username: user?.username ?? '',
        email: user?.email ?? '',
        displayName: user?.displayName ?? '',
        avatar,
        //#if (LocalIdentity)
        password: '',
        //#endif
        isActive: user?.isActive ?? true,
        //#if (LocalIdentity)
        isEmailVerified: user?.isEmailVerified ?? false,
        //#endif
        roleIds: user ? (user.roles ?? []).map((role) => role.id) : [],
      });
      this.avatarPreview.set(avatar);
    });
  }

  isEditMode() {
    return !!this.user();
  }

  hasAvatarImage() {
    return isAvatarImageUrl(this.avatarPreview());
  }

  /**
   * 选完图片先在浏览器里裁成正方形、缩到 256 再放进表单：服务端对头像有体积上限，
   * 不处理就提交原图多半会被拒（与个人资料面板共用同一处理）。
   */
  async onAvatarSelect(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // 允许再次选择同一文件时仍触发 change 事件
    input.value = '';
    if (!file) {
      return;
    }

    try {
      const result = await prepareAvatarImage(file);
      this.formModel.update((m) => ({ ...m, avatar: result }));
      this.avatarPreview.set(result);
    } catch (error: unknown) {
      const messages = this.avatarMessages();
      const reason = error instanceof AvatarImageRejected ? error.reason : 'decode';
      if (reason === 'type') {
        toast.error(messages.typeSummary, { description: messages.typeDetail });
      } else if (reason === 'size') {
        toast.error(messages.sizeSummary, { description: messages.sizeDetail });
      } else {
        toast.error(messages.typeSummary, { description: messages.decodeDetail });
      }
    }
  }

  // hlm-switch 为 CVA（checked 非 ModelSignal），Signal Forms 的 [formField] 不适配；
  // 直接以 [checked]/(checkedChange) 双向绑定回写模型信号。
  setActive(checked: boolean): void {
    this.formModel.update((m) => ({ ...m, isActive: checked }));
  }
  //#if (LocalIdentity)
  setEmailVerified(checked: boolean): void {
    this.formModel.update((m) => ({ ...m, isEmailVerified: checked }));
  }
  //#endif

  /** 桥接 hlm-dialog 声明式 state 到对外 visible 契约。 */
  onDialogStateChange(state: BrnDialogState): void {
    this.visible.set(state === 'open');
  }

  onHide() {
    this.visible.set(false);
  }

  save() {
    if (this.userForm().invalid()) {
      this.userForm().markAsTouched();
      return;
    }

    const model = this.formModel();
    if (this.isEditMode()) {
      this.saved.emit({
        email: model.email,
        displayName: model.displayName.trim() || undefined,
        avatar: model.avatar.trim() || undefined,
        //#if (LocalIdentity)
        isEmailVerified: model.isEmailVerified,
        //#endif
      });
      return;
    }

    this.saved.emit({
      username: model.username.trim(),
      email: model.email,
      displayName: model.displayName.trim() || undefined,
      avatar: model.avatar.trim() || undefined,
      //#if (LocalIdentity)
      password: model.password,
      //#endif
      isActive: model.isActive,
      //#if (LocalIdentity)
      isEmailVerified: model.isEmailVerified,
      //#endif
      roleIds: this.canAssignRoles() ? model.roleIds : [],
    });
  }
  //#if (LocalIdentity)
  protected readonly showPassword = signal(false);
  //#endif
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'users.editDialog.editHeader': 'Edit user',
  'users.editDialog.createHeader': 'New user',
  'users.editDialog.createDescription':
    'Fill in the account profile and set its initial roles and status.',
  'users.editDialog.editDescription':
    'Update the account profile. Roles and sign-in access are managed from the user list.',
  'users.editDialog.loading': 'Loading user information...',
  'common.uploadAvatar': 'Upload avatar',
  'users.editDialog.unnamedUser': 'Unnamed user',
  'users.editDialog.noEmail': 'No email set',
  'users.editDialog.avatarHint': 'PNG, JPG or WebP',
  'users.editDialog.usernameLabel': 'Username',
  'users.editDialog.usernamePlaceholder': 'Enter a username',
  'users.editDialog.emailLabel': 'Email',
  'users.editDialog.emailPlaceholder': 'Enter an email',
  'users.editDialog.displayNameLabel': 'Display name',
  'users.editDialog.displayNamePlaceholder': 'Enter a display name',
  'users.editDialog.passwordLabel': 'Initial password',
  'users.editDialog.passwordPlaceholder': 'At least 12 characters',
  'common.hidePassword': 'Hide password',
  'common.showPassword': 'Show password',
  'users.editDialog.rolesLabel': 'Roles',
  'users.editDialog.rolesPlaceholder': 'Select roles',
  'users.editDialog.activeTitle': 'Enable user',
  'users.editDialog.activeHint':
    'When disabled, the user cannot sign in or call protected endpoints',
  'users.editDialog.emailVerifiedTitle': 'Email verified',
  'users.editDialog.emailVerifiedHint': 'Marks the address as confirmed to belong to this user',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
  'common.create': 'Create',
};
//#endif
