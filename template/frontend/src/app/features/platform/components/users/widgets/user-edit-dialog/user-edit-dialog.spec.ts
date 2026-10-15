import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { toast } from '@spartan-ng/brain/sonner';

import { UserEditDialog } from './user-edit-dialog';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { PASSWORD_MIN_LENGTH } from '../../../../../../core/validation/password-rule';
import {
  expectTouchTarget,
  setCoarsePointer,
  tapAt,
} from '../../../../../../shared/utils/coarse-pointer.testing';
import { RoleBriefDto } from '../../../../dtos/role.dto';
import {
  CreateUserInputDto,
  UpdateUserInputDto,
  UserManagementOutputDto,
} from '../../../../dtos/user-management.dto';

/**
 * 用户新建与编辑共用一个对话框，两种模式的字段集与提交载荷不同：
 * 新建带用户名、初始口令、启用状态与（有权时的）初始角色；编辑只改资料，
 * 用户名不可改，角色与启停各有独立入口和权限。保存请求与失败提示由用户页处理。
 */
@Component({
  imports: [UserEditDialog],
  template: `
    <app-user-edit-dialog
      [(visible)]="visible"
      [loading]="loading()"
      [saving]="saving()"
      [user]="user()"
      [availableRoles]="roles"
      [canAssignRoles]="canAssignRoles()"
      (saved)="saved.push($event)"
    />
  `,
})
class HostComponent {
  readonly visible = signal(true);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly user = signal<UserManagementOutputDto | null>(null);
  readonly canAssignRoles = signal(false);
  readonly roles: RoleBriefDto[] = [
    { id: 'role-admin', name: 'admin', displayName: 'Administrator' },
    { id: 'role-auditor', name: 'auditor', displayName: 'Auditor' },
  ];
  readonly saved: (CreateUserInputDto | UpdateUserInputDto)[] = [];
}

//#if (!IncludeLocalization)
/** 与组件内英文文案一致。 */
const ENGLISH_HINTS: Record<string, string> = {
  'users.editDialog.activeHint':
    'When disabled, the user cannot sign in or call protected endpoints',
  'users.editDialog.emailVerifiedHint': 'Used to manage the email verification status',
};

//#endif
describe('UserEditDialog', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  const existing: UserManagementOutputDto = {
    id: 'user-1',
    username: 'alice',
    email: 'alice@example.test',
    displayName: 'Alice',
    isActive: true,
    isEmailVerified: true,
    isSuperAdmin: false,
    roles: [{ id: 'role-auditor', name: 'auditor', displayName: 'Auditor' }],
    creationTime: '2026-08-14T00:00:00Z',
  };

  type Field = 'username' | 'email' | 'displayName' | 'password';

  function dialog(): UserEditDialog {
    return fixture.debugElement.query(By.directive(UserEditDialog))
      .componentInstance as UserEditDialog;
  }

  /** 对话框渲染在 document 上的浮层里。 */
  function submitButton(): HTMLButtonElement {
    return document.querySelector<HTMLButtonElement>('hlm-dialog-footer button[type="submit"]')!;
  }

  function errorKinds(field: Field): string[] {
    return dialog()
      .userForm[field]()
      .errors()
      .map((error) => error.kind);
  }

  /** 显示出来的校验提示：未触碰的字段错误元素在，但处于 hidden。 */
  function shownErrorCount(): number {
    return Array.from(document.querySelectorAll<HTMLElement>('hlm-field-error')).filter(
      (element) => !element.hidden,
    ).length;
  }

  async function fill(values: Partial<Record<Field, string>>): Promise<void> {
    for (const [field, value] of Object.entries(values)) {
      dialog().userForm[field as Field]().value.set(value);
    }
    await fixture.whenStable();
  }

  async function fillValidCreate(): Promise<void> {
    await fill({
      username: 'bob_01',
      email: 'bob@example.test',
      displayName: '   ',
      password: ' Create!Passw0rd ',
    });
  }

  async function switchToEdit(): Promise<void> {
    host.user.set(existing);
    await fixture.whenStable();
  }

  beforeEach(async () => {
    vi.spyOn(toast, 'error').mockImplementation(() => '');

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

  it('associates every field and switch label with its control', () => {
    for (const id of [
      'user-username',
      'user-email',
      'user-display-name',
      'user-password',
      'user-is-active',
      'user-email-verified',
    ]) {
      expect(document.querySelector(`label[for="${id}"]`)).not.toBeNull();
      expect(document.getElementById(id)).not.toBeNull();
    }
  });

  // 开关的说明与标签同属一个字段：说明随开关一起出现，读的是该开关自己的提示
  it('describes each switch inside its own field', () => {
    for (const [id, hint] of [
      ['user-is-active', 'users.editDialog.activeHint'],
      //#if (LocalIdentity)
      ['user-email-verified', 'users.editDialog.emailVerifiedHint'],
      //#endif
    ]) {
      const field = document.querySelector(`label[for="${id}"]`)!.closest('[hlmField]')!;
      const description = field.querySelector('[hlmFieldDescription]')?.textContent?.trim();
      //#if (IncludeLocalization)
      // 测试不装词条，缺失的键原样渲染：正好能看出读的是哪一条
      expect(description).toBe(hint);
      //#else
      expect(description).toBe(ENGLISH_HINTS[hint]);
      //#endif
      expect(field.contains(document.getElementById(id))).toBe(true);
      // 读屏软件聚焦开关时要连同说明一起读：开关按钮的 aria-describedby 指向这条说明
      const describedBy = field.querySelector('[role="switch"]')?.getAttribute('aria-describedby');
      expect(describedBy).toBeTruthy();
      expect(document.getElementById(describedBy!)?.textContent?.trim()).toBe(description);
    }
  });

  // 开关视觉只有约 32×18px：触屏下命中区铺到 44px，点在开关外沿也能切换
  it('toggles the active switch from the edge of its touch target on coarse pointers', async () => {
    await setCoarsePointer(true);
    onTestFinished(() => setCoarsePointer(false));
    // 弹窗打开时有缩放动画，量尺寸前等它结束
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
    const active = document.getElementById('user-is-active')!;
    const before = active.getAttribute('aria-checked');

    expectTouchTarget(active);
    await tapAt(active, 0, 20);
    await fixture.whenStable();

    expect(active.getAttribute('aria-checked')).toBe(before === 'true' ? 'false' : 'true');
  });

  it('does not submit an empty create form and reveals the errors on submit', async () => {
    expect(shownErrorCount()).toBe(0);

    submitButton().click();
    await fixture.whenStable();

    expect(host.saved).toEqual([]);
    expect(errorKinds('username')).toContain('required');
    expect(errorKinds('email')).toContain('required');
    expect(errorKinds('password')).toContain('required');
    expect(shownErrorCount()).toBeGreaterThan(0);
  });

  // 与服务端规则一致：长度 3~64、字母数字下划线，长度与字符集共用一条提示
  it.each(['ab', 'bob smith', 'bob-smith', '用户名', 'a'.repeat(65)])(
    'rejects %j as a username',
    async (username) => {
      await fillValidCreate();
      await fill({ username });

      expect(errorKinds('username')).toEqual(['usernamePattern']);
      submitButton().click();
      expect(host.saved).toEqual([]);
    },
  );

  // 与后端口径一致：首尾空白不是邮箱的一部分。这里拒绝而不是替用户 trim，提交的值即用户看到的值
  it('rejects an email with surrounding whitespace instead of trimming it', async () => {
    await fillValidCreate();
    await fill({ email: ' bob@example.test ' });

    expect(errorKinds('email')).toEqual(['email']);
    submitButton().click();
    expect(host.saved).toEqual([]);
  });

  it('rejects an invalid email and a short initial password', async () => {
    await fillValidCreate();
    await fill({ email: 'not-an-email', password: 'x'.repeat(PASSWORD_MIN_LENGTH - 1) });

    expect(errorKinds('email')).toEqual(['email']);
    expect(errorKinds('password')).toEqual(['minLength']);
    submitButton().click();
    expect(host.saved).toEqual([]);
  });

  // 口令原样提交（空白是口令的一部分），空白显示名不提交；无角色分配权限时即便模型里有角色也不提交
  it('sends the trimmed create payload with the password as typed and no roles without permission', async () => {
    await fillValidCreate();
    dialog().userForm.roleIds().value.set(['role-admin']);
    await fixture.whenStable();

    submitButton().click();

    expect(host.saved).toEqual([
      {
        username: 'bob_01',
        email: 'bob@example.test',
        displayName: undefined,
        avatar: undefined,
        password: ' Create!Passw0rd ',
        isActive: true,
        isEmailVerified: false,
        roleIds: [],
      },
    ]);
  });

  it('shows the role field and submits the picked roles only when roles may be assigned', async () => {
    expect(document.getElementById('user-roles')).toBeNull();

    host.canAssignRoles.set(true);
    await fixture.whenStable();
    expect(document.getElementById('user-roles')).not.toBeNull();

    await fillValidCreate();
    dialog().userForm.roleIds().value.set(['role-admin', 'role-auditor']);
    dialog().setActive(false);
    await fixture.whenStable();

    submitButton().click();

    expect(host.saved[0]).toEqual(
      expect.objectContaining({ roleIds: ['role-admin', 'role-auditor'], isActive: false }),
    );
  });

  it('refills from the user in edit mode, locks the username and hides create-only fields', async () => {
    host.canAssignRoles.set(true);
    await switchToEdit();

    expect(dialog().userForm.username().value()).toBe('alice');
    expect(dialog().userForm.username().disabled()).toBe(true);
    expect(dialog().userForm.email().value()).toBe('alice@example.test');
    // 编辑态的角色与启停各有独立入口和权限：这里再放一份就是第二个入口
    expect(document.getElementById('user-password')).toBeNull();
    expect(document.getElementById('user-roles')).toBeNull();
    expect(document.getElementById('user-is-active')).toBeNull();
    // 口令字段不渲染也不能参与校验，否则编辑永远保存不了
    expect(dialog().userForm().invalid()).toBe(false);
  });

  it('sends only the profile fields on edit', async () => {
    await switchToEdit();
    await fill({ email: 'alice@new.example.test', displayName: '  Alice Smith  ' });
    dialog().setEmailVerified(false);
    await fixture.whenStable();

    submitButton().click();

    expect(host.saved).toEqual([
      {
        email: 'alice@new.example.test',
        displayName: 'Alice Smith',
        avatar: undefined,
        isEmailVerified: false,
      },
    ]);
  });

  it('blocks a second submit while the save is in flight', async () => {
    await fillValidCreate();
    host.saving.set(true);
    await fixture.whenStable();

    expect(submitButton().disabled).toBe(true);
    submitButton().click();
    expect(host.saved).toEqual([]);
  });

  it('shows the loading state instead of the form while the user loads', async () => {
    host.loading.set(true);
    await fixture.whenStable();

    expect(document.getElementById('user-username')).toBeNull();
    expect(document.querySelector('app-dialog-loading')).not.toBeNull();
  });

  // 对话框不销毁：上一次编辑的资料留到新建，会带着别人的邮箱建出一个新账号
  it('resets to a blank create form when reopened after editing', async () => {
    await switchToEdit();
    host.visible.set(false);
    await fixture.whenStable();

    host.user.set(null);
    host.visible.set(true);
    await fixture.whenStable();

    expect(dialog().userForm.username().value()).toBe('');
    expect(dialog().userForm.username().disabled()).toBe(false);
    expect(dialog().userForm.email().value()).toBe('');
    expect(dialog().userForm.roleIds().value()).toEqual([]);
  });

  it('rejects a non-image avatar with a message and keeps the current avatar', async () => {
    await switchToEdit();
    const picker = document.querySelector<HTMLInputElement>('input[type="file"]')!;
    const files = new DataTransfer();
    files.items.add(new File(['plain'], 'notes.txt', { type: 'text/plain' }));
    picker.files = files.files;

    await dialog().onAvatarSelect({ target: picker } as unknown as Event);
    await fixture.whenStable();

    expect(toast.error).toHaveBeenCalledOnce();
    expect(dialog().avatarPreview()).toBe('');
    // 清空选择：同一个文件再选一次仍要触发 change
    expect(picker.value).toBe('');
  });
});
