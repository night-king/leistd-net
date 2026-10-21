import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  model,
  output,
  signal,
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckboxImports } from '@spartan-ng/helm/checkbox';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmSpinner } from '@spartan-ng/helm/spinner';

import { applicationErrorMessage } from '../../../../../../core/errors/application-http-error';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import { RoleBriefDto } from '../../../../dtos/role.dto';
import { UserManagementOutputDto } from '../../../../dtos/user-management.dto';
import { UserManagementService } from '../../../../services/user-management-service';

/** 用户角色分配对话框：与资料编辑分离，只调用 `PUT /api/v1/users/{id}/roles`（要求 `App.Users.ManageRoles`）。 */
@Component({
  selector: 'app-user-roles-dialog',
  // prettier-ignore
  imports: [
    HlmButton,
    HlmSpinner,
    ...HlmDialogImports,
    ...HlmCheckboxImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './user-roles-dialog.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserRolesDialog {
  readonly open = model(false);
  readonly user = input<UserManagementOutputDto | null>(null);
  readonly availableRoles = input<RoleBriefDto[]>([]);
  readonly saved = output<void>();

  private readonly userService = inject(UserManagementService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly saving = signal(false);
  private readonly selected = signal<ReadonlySet<string>>(new Set<string>());

  constructor() {
    effect(() => {
      const user = this.user();
      if (!this.open() || !user) {
        return;
      }

      this.selected.set(new Set((user.roles ?? []).map((role) => role.id)));
    });
  }

  isSelected(roleId: string): boolean {
    return this.selected().has(roleId);
  }

  toggle(roleId: string): void {
    const next = new Set(this.selected());
    if (next.has(roleId)) {
      next.delete(roleId);
    } else {
      next.add(roleId);
    }
    this.selected.set(next);
  }

  onSave(): void {
    const user = this.user();
    if (!user) {
      return;
    }

    this.saving.set(true);
    this.userService.replaceUserRoles(user.id, { roleIds: [...this.selected()] }).subscribe({
      next: () => {
        this.saving.set(false);
        //#if (IncludeLocalization)
        toast.success(this.transloco.translate('users.rolesDialog.saved'));
        //#else
        toast.success('Roles updated');
        //#endif
        this.saved.emit();
      },
      error: (error) => {
        this.saving.set(false);
        toast.error(applicationErrorMessage(error));
      },
    });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'users.rolesDialog.title': 'Roles · {{name}}',
  'users.rolesDialog.description':
    'The user inherits every permission granted to the selected roles.',
  'common.noData': 'No data',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
};
//#endif
