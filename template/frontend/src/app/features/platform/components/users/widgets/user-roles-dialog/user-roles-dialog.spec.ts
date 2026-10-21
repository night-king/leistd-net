import { HttpErrorResponse } from '@angular/common/http';
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { toast } from '@spartan-ng/brain/sonner';
import { Observable, of, Subject, throwError } from 'rxjs';

import { UserRolesDialog } from './user-roles-dialog';
import { ApplicationHttpError } from '../../../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import {
  expectTouchTarget,
  setCoarsePointer,
  tapAt,
} from '../../../../../../shared/utils/coarse-pointer.testing';
import { RoleBriefDto } from '../../../../dtos/role.dto';
import {
  UpdateUserRolesInputDto,
  UserManagementOutputDto,
} from '../../../../dtos/user-management.dto';
import { UserManagementService } from '../../../../services/user-management-service';

import type { Mock } from 'vitest';

/**
 * 用户角色分配：本对话框自己发 `PUT /users/{id}/roles`，提交的是整组角色 Id（替换语义）。
 *
 * 提交集合算错的后果是静默改权：漏一个就收回了一个角色，多一个就多授了一组权限。
 */
@Component({
  imports: [UserRolesDialog],
  template: `
    <app-user-roles-dialog
      [(open)]="open"
      [user]="user()"
      [availableRoles]="roles"
      (saved)="savedCount = savedCount + 1"
    />
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly user = signal<UserManagementOutputDto | null>(null);
  readonly roles: RoleBriefDto[] = [
    { id: 'role-admin', name: 'admin', displayName: 'Administrator' },
    { id: 'role-auditor', name: 'auditor', displayName: 'Auditor' },
    { id: 'role-viewer', name: 'viewer', displayName: 'Viewer' },
  ];
  savedCount = 0;
}

describe('UserRolesDialog', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let replaceUserRoles: Mock<(id: string, data: UpdateUserRolesInputDto) => Observable<unknown>>;

  const alice: UserManagementOutputDto = {
    id: 'user-1',
    username: 'alice',
    email: 'alice@example.test',
    isActive: true,
    //#if (LocalIdentity)
    isEmailVerified: true,
    //#endif
    isSuperAdmin: false,
    roles: [{ id: 'role-auditor', name: 'auditor', displayName: 'Auditor' }],
    creationTime: '2026-08-14T00:00:00Z',
  };

  /** 对话框渲染在 document 上的浮层里。 */
  function saveButton(): HTMLButtonElement {
    return document.querySelector<HTMLButtonElement>('hlm-dialog-footer button:last-of-type')!;
  }

  function submittedRoleIds(call = -1): string[] {
    return [...replaceUserRoles.mock.calls.at(call)![1].roleIds].sort();
  }

  async function toggle(roleId: string): Promise<void> {
    document.querySelector<HTMLButtonElement>(`#role-${roleId}`)!.click();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    replaceUserRoles = vi.fn().mockName('UserManagementService.replaceUserRoles');
    replaceUserRoles.mockReturnValue(of([]));
    vi.spyOn(toast, 'success').mockImplementation(() => '');
    vi.spyOn(toast, 'error').mockImplementation(() => '');

    TestBed.configureTestingModule({
      imports: [HostComponent],
      // prettier-ignore
      providers: [
        provideZonelessChangeDetection(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: UserManagementService, useValue: { replaceUserRoles } },
      ],
    });

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.user.set(alice);
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  it('labels every role checkbox with the role name', () => {
    for (const role of host.roles) {
      const label = document.querySelector(`label[for="role-${role.id}"]`);
      expect(label?.textContent).toContain(role.displayName);
      expect(document.getElementById(`role-${role.id}`)).not.toBeNull();
    }
  });

  // 列表滚动区左侧没有内边距：触屏下行首要留出 14px，复选框命中区的左沿才不被裁掉
  it('keeps the left edge of each role checkbox tappable on coarse pointers', async () => {
    await setCoarsePointer(true);
    onTestFinished(() => setCoarsePointer(false));
    await fixture.whenStable();
    // 弹窗打开时有缩放动画，量尺寸前等它结束
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
    const dialog = fixture.debugElement.query(By.directive(UserRolesDialog))
      .componentInstance as UserRolesDialog;

    for (const role of host.roles) {
      expectTouchTarget(document.getElementById(`role-${role.id}`)!);
    }

    await tapAt(document.getElementById('role-role-admin')!, -20, 0);
    await fixture.whenStable();
    expect(dialog.isSelected('role-admin')).toBe(true);
  });

  it('starts from the roles the user already has', () => {
    const dialog = fixture.debugElement.query(By.directive(UserRolesDialog))
      .componentInstance as UserRolesDialog;

    expect(dialog.isSelected('role-auditor')).toBe(true);
    expect(dialog.isSelected('role-admin')).toBe(false);
  });

  // 替换语义：提交的是勾选后的完整集合，未改动的既有角色也要在里面
  it('submits the complete selection, keeping untouched roles and dropping unchecked ones', async () => {
    await toggle('role-admin');
    await toggle('role-viewer');
    await toggle('role-viewer');

    saveButton().click();
    await fixture.whenStable();

    expect(replaceUserRoles).toHaveBeenCalledOnce();
    expect(replaceUserRoles.mock.calls[0][0]).toBe('user-1');
    expect(submittedRoleIds()).toEqual(['role-admin', 'role-auditor']);
    expect(toast.success).toHaveBeenCalledOnce();
    expect(host.savedCount).toBe(1);
  });

  it('submits an empty list when every role is removed', async () => {
    await toggle('role-auditor');

    saveButton().click();
    await fixture.whenStable();

    expect(submittedRoleIds()).toEqual([]);
  });

  it('blocks a second submit while the request is in flight', async () => {
    const response = new Subject<RoleBriefDto[]>();
    replaceUserRoles.mockReturnValue(response);

    saveButton().click();
    await fixture.whenStable();
    expect(saveButton().disabled).toBe(true);
    saveButton().click();
    await fixture.whenStable();

    expect(replaceUserRoles).toHaveBeenCalledOnce();

    response.next([]);
    response.complete();
    await fixture.whenStable();
    expect(saveButton().disabled).toBe(false);
  });

  it('shows the server reason, keeps the selection and allows retrying when saving fails', async () => {
    const rejected = ApplicationHttpError.from(
      new HttpErrorResponse({
        status: 400,
        error: { errors: [{ field: 'roleIds', detail: 'Role does not exist.', code: 'X:Y' }] },
      }),
    );
    replaceUserRoles.mockReturnValue(throwError(() => rejected));
    await toggle('role-admin');

    saveButton().click();
    await fixture.whenStable();

    expect(toast.error).toHaveBeenCalledExactlyOnceWith('Role does not exist.');
    expect(toast.success).not.toHaveBeenCalled();
    expect(host.savedCount).toBe(0);
    expect(saveButton().disabled).toBe(false);

    replaceUserRoles.mockReturnValue(of([]));
    saveButton().click();
    await fixture.whenStable();
    expect(submittedRoleIds()).toEqual(['role-admin', 'role-auditor']);
  });

  // 对话框不销毁：上一个用户的勾选留到下一个用户，提交即替换掉对方的角色
  it('reloads the selection from the next user when reopened', async () => {
    await toggle('role-admin');
    host.open.set(false);
    await fixture.whenStable();

    host.user.set({ ...alice, id: 'user-2', username: 'bob', roles: [] });
    host.open.set(true);
    await fixture.whenStable();

    saveButton().click();
    await fixture.whenStable();
    expect(replaceUserRoles.mock.calls[0][0]).toBe('user-2');
    expect(submittedRoleIds()).toEqual([]);
  });

  it('sends nothing without a user', async () => {
    host.user.set(null);
    await fixture.whenStable();

    saveButton().click();
    await fixture.whenStable();

    expect(replaceUserRoles).not.toHaveBeenCalled();
  });
});
