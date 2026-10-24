import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  output,
  signal,
} from '@angular/core';
import { form, FormField, maxLength, required } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideFingerprint, lucideInfo, lucidePlus, lucideTriangleAlert } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmItemImports } from '@spartan-ng/helm/item';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { lastValueFrom } from 'rxjs';

import {
  ReauthenticateDialog,
  ReauthenticationProof,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import { API_ERROR_CODES } from '../../../../core/errors/api-error-codes';
import {
  ApplicationHttpError,
  applicationErrorMessage,
} from '../../../../core/errors/application-http-error';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
import { AuthService } from '../../../../core/services/auth-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { PASSKEY_NAME_MAX_LENGTH } from '../../../../core/webauthn/passkey-device-name';
import { getPasskey, passkeySupport } from '../../../../core/webauthn/webauthn';
import { StatusDot } from '../../../../shared/directives/status-dot';
import {
  PasskeyOutputDto,
  TwoFactorStatusOutputDto,
} from '../../../../shared/dtos/account-security.dto';
import { AppDate } from '../../../../shared/pipes/app-date-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';
import { injectPasskeyRegistration, notifyWebAuthnFailure } from '../../utils/passkey-registration';
import { canChooseDefault } from '../../utils/two-factor-methods';
import { RecoveryCodes } from '../recovery-codes/recovery-codes';

/** 剩余恢复码不多于这个数时提醒重新生成（与身份验证器一节一致）。 */
const RECOVERY_CODES_LOW = 3;
const RENAME_INPUT_ID = 'passkey-rename-input';

/** 添加按钮为什么不可用；可用时为 null。 */
type PasskeyUnavailableReason = 'notConfigured' | 'unsupported' | 'insecureContext';

/**
 * 「账户与安全」面板的一节：本人的通行密钥。列表（名称、登记时间、最近使用、备份状态）、添加、行内改名与删除，
 * 与身份验证器、邮箱验证码各节并列；有别的方式可选时标出"默认"或给"设为默认"。
 *
 * 添加先确认是本人（会话不够新时由服务端要求），再由浏览器创建。确认身份之后本人要再点一次"创建通行密钥"：
 * 浏览器要求创建发生在用户手势里，对话框关掉时那次点击已经用掉了。添加、删除都会让本人其他设备退出，
 * 面板据 {@link sessionsChanged} 刷新设备列表。第一把通行密钥是第一个独立因子时，服务端一并发下恢复码，
 * 在这里一次性展示；只有通行密钥（没有身份验证器）的人在这一节查看与重新生成恢复码，用通行密钥证明。
 *
 * 部署没配依赖方、浏览器不支持或不在安全上下文时添加按钮不可用，并写明原因。
 */
@Component({
  selector: 'app-two-factor-passkey-settings',
  // prettier-ignore
  imports: [
    AppDate,
    FormField,
    NgIcon,
    ...HlmAlertImports,
    HlmBadge,
    HlmButton,
    ...HlmFieldImports,
    HlmInput,
    ...HlmItemImports,
    HlmSpinner,
    ReauthenticateDialog,
    RecoveryCodes,
    StatusDot,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideFingerprint, lucideInfo, lucidePlus, lucideTriangleAlert })],
  templateUrl: './two-factor-passkey-settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TwoFactorPasskeySettings {
  private readonly accountService = inject(AccountService);
  private readonly authService = inject(AuthService);
  private readonly confirmService = inject(ConfirmService);
  private readonly settingContext = inject(SettingContextService);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /** 本人的其他会话被服务端撤销了（添加、删除通行密钥时）。 */
  readonly sessionsChanged = output<void>();
  /** 登录默认方式或可用方式变了（设为默认、添加、删除）：面板据此刷新其他各节。 */
  readonly defaultChanged = output<void>();

  /** 添加的共同流程；它的确认身份状态也给删除用（同一时刻只有一个挂起的操作）。 */
  protected readonly registration = injectPasskeyRegistration({
    text: (key) => this.text(key),
    registered: (result) => {
      this.codes.set(result.recoveryCodes);
      this.afterFactorsChanged();
      toast.success(this.text('account.passkeys.addedToast'));
    },
    failed: (error) => this.showError(error),
    awaitingGesture: () => this.focus('passkey-continue'),
  });
  protected readonly reauth = this.registration.reauth;
  protected readonly displayTimeZone = this.settingContext.timeZone;
  protected readonly displayLocale = this.settingContext.displayLocale;

  protected readonly status = signal<TwoFactorStatusOutputDto | null>(null);
  protected readonly loadFailed = signal(false);
  protected readonly busy = signal(false);
  /** 有操作在途（改名、删除、设为默认、重新生成，或添加）：其余操作按钮不可用。 */
  protected readonly working = computed(() => this.busy() || this.registration.adding());
  /** 正在删除的通行密钥 Id：只在那一行显示转圈。 */
  protected readonly removingId = signal<string | null>(null);
  /** 刚发下的恢复码（第一个独立因子，或重新生成）：一次性展示。 */
  protected readonly codes = signal<string[]>([]);

  protected readonly renamingId = signal<string | null>(null);
  private readonly renameModel = signal({ name: '' });
  protected readonly renameForm = form(this.renameModel, (path) => {
    required(path.name);
    maxLength(path.name, PASSKEY_NAME_MAX_LENGTH);
  });
  protected readonly renameInputId = RENAME_INPUT_ID;

  protected readonly passkeys = computed(() => this.status()?.passkeys ?? []);
  protected readonly unavailableReason = computed<PasskeyUnavailableReason | null>(() => {
    const status = this.status();
    if (status && !status.passkeysAvailable) {
      return 'notConfigured';
    }
    const support = passkeySupport();
    return support === 'supported' ? null : support;
  });
  /** 能选默认方式时（这种方式能用、还有别的可选）标出"默认"或给"设为默认"。 */
  protected readonly defaultChoice = computed(() => canChooseDefault(this.status(), 'passkey'));
  /** 独立因子只有通行密钥：恢复码在这一节管理，并提示再加一把或身份验证器。 */
  protected readonly onlyPasskeys = computed(() => {
    const status = this.status();
    return !!status && !status.enabled && status.passkeys.length > 0;
  });
  protected readonly recoveryCodesLow = computed(
    () => this.onlyPasskeys() && (this.status()?.recoveryCodesLeft ?? 0) <= RECOVERY_CODES_LOW,
  );
  /** 组织要求两步验证、它又是最后一个独立因子：不能删，按钮不可用并说明。 */
  protected readonly removalBlocked = computed(() => {
    const status = this.status();
    return !!status?.requiredByPolicy && !status.enabled && status.passkeys.length === 1;
  });

  constructor() {
    this.reload();
  }

  /** 重新读取状态；其他各节改了默认方式或启停后由面板调用。 */
  reload(): void {
    this.loadFailed.set(false);
    this.accountService.getTwoFactorStatus().subscribe({
      next: (status) => this.status.set(status),
      error: () => this.loadFailed.set(true),
    });
  }

  protected onReauthVisibleChange(visible: boolean): void {
    const saving = this.reauth.saving();
    this.registration.setReauthVisible(visible);
    if (!visible && !saving) {
      this.busy.set(false);
      this.removingId.set(null);
    }
  }

  protected beginRename(passkey: PasskeyOutputDto): void {
    this.renamingId.set(passkey.id);
    this.renameModel.set({ name: passkey.name });
    this.renameForm().reset();
    this.focus(RENAME_INPUT_ID);
  }

  protected cancelRename(): void {
    this.renamingId.set(null);
  }

  protected saveRename(passkey: PasskeyOutputDto): void {
    const name = this.renameModel().name.trim();
    if (this.working()) {
      return;
    }
    if (this.renameForm().invalid() || !name) {
      this.renameForm().markAsTouched();
      return;
    }
    if (name === passkey.name) {
      this.renamingId.set(null);
      return;
    }
    this.busy.set(true);
    this.accountService.renamePasskey(passkey.id, name).subscribe({
      next: (renamed) => {
        this.busy.set(false);
        this.renamingId.set(null);
        this.status.update((status) =>
          status
            ? {
                ...status,
                passkeys: status.passkeys.map((item) => (item.id === renamed.id ? renamed : item)),
              }
            : status,
        );
        toast.success(this.text('account.passkeys.renamedToast'));
      },
      error: (error) => {
        this.busy.set(false);
        this.showError(error);
      },
    });
  }

  /** 删除前先确认；会话不够新时服务端要求确认身份，凭证随请求体出示。 */
  protected async remove(passkey: PasskeyOutputDto): Promise<void> {
    if (this.working() || this.removalBlocked()) {
      return;
    }
    this.busy.set(true);
    this.removingId.set(passkey.id);
    const confirmed = await this.confirmService.open({
      message: this.text('account.passkeys.removeConfirm', { name: passkey.name }),
      confirmText: this.text('account.passkeys.remove'),
      variant: 'destructive',
    });
    if (!confirmed) {
      this.busy.set(false);
      this.removingId.set(null);
      return;
    }
    this.removeConfirmed(passkey);
  }

  private removeConfirmed(passkey: PasskeyOutputDto, proof?: ReauthenticationProof): void {
    this.accountService
      .removePasskey(passkey.id, {
        password: proof?.password,
        twoFactorCode: proof?.twoFactorCode,
        passkey: proof?.passkey,
      })
      .subscribe({
        next: () => {
          this.reauth.complete();
          this.busy.set(false);
          this.removingId.set(null);
          this.afterFactorsChanged();
          toast.success(this.text('account.passkeys.removedToast'));
        },
        error: (error) => {
          if (
            this.reauth.handleError(error, proof, (next) => this.removeConfirmed(passkey, next))
          ) {
            return;
          }
          this.busy.set(false);
          this.removingId.set(null);
          this.showError(error);
        },
      });
  }

  /** 登录时默认先给通行密钥；不增减方式，不要求确认身份。 */
  protected setAsDefault(): void {
    if (this.working()) {
      return;
    }
    this.busy.set(true);
    this.accountService.setPreferredTwoFactorMethod('passkey').subscribe({
      next: () => {
        this.busy.set(false);
        this.reload();
        this.defaultChanged.emit();
        toast.success(this.text('account.twoFactor.defaultToast'));
      },
      error: (error) => {
        this.busy.set(false);
        this.showError(error);
      },
    });
  }

  /**
   * 只有通行密钥的人重新生成恢复码：用通行密钥证明（这次点击里调起浏览器），不认口令与恢复码。
   */
  protected async regenerate(): Promise<void> {
    if (this.working()) {
      return;
    }
    this.busy.set(true);
    try {
      const { options } = await lastValueFrom(this.authService.beginPasskeyReauthentication());
      const passkey = await getPasskey(options);
      const result = await lastValueFrom(this.accountService.regenerateRecoveryCodes({ passkey }));
      this.codes.set(result.recoveryCodes);
      this.reload();
    } catch (error) {
      this.showError(error);
    } finally {
      this.busy.set(false);
    }
  }

  protected finishCodes(): void {
    this.codes.set([]);
  }

  // 可用方式变了：刷新本节、当前用户（确认身份能用通行密钥、受限会话随之解除）与其他各节
  private afterFactorsChanged(): void {
    this.reload();
    this.authService.loadUser().subscribe({ error: () => undefined });
    this.sessionsChanged.emit();
    this.defaultChanged.emit();
  }

  /**
   * 浏览器这一侧的失败按归类给本地化提示（取消只是中性提示），不显示浏览器原文；服务端的原因原样显示，
   * 部署此刻没配依赖方时顺带刷新，按钮随之不可用。
   */
  private showError(error: unknown): void {
    if (notifyWebAuthnFailure(error, (key) => this.text(key))) {
      return;
    }
    if (
      error instanceof ApplicationHttpError &&
      error.code === API_ERROR_CODES.passkeyUnavailable
    ) {
      this.reload();
    }
    toast.error(this.text('common.requestError'), {
      description: applicationErrorMessage(error),
    });
  }

  private text(key: string, params?: Record<string, unknown>): string {
    //#if (IncludeLocalization)
    return this.transloco.translate(key, params);
    //#else
    return this.t(key, params);
    //#endif
  }

  // 改名输入与"创建通行密钥"按钮在本次变更检测之后才渲染：渲染完成后再聚焦
  private focus(id: string): void {
    afterNextRender(() => this.document.getElementById(id)?.focus(), {
      injector: this.injector,
    });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.requestError': 'Request error',
  'common.retry': 'Retry',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
  'account.twoFactor.default': 'Default',
  'account.twoFactor.setDefault': 'Set as default',
  'account.twoFactor.defaultToast': 'Saved. Sign-in will ask for this method first.',
  'account.twoFactor.regenerate': 'Regenerate recovery codes',
  'account.twoFactor.recoveryCodesLeft': '{{count}} unused recovery code(s) left',
  'account.passkeys.header': 'Passkeys',
  'account.passkeys.subtitle':
    "Sign in with your fingerprint, face or screen lock instead of a password. Passkeys can't be phished.",
  'account.passkeys.on': 'On',
  'account.passkeys.add': 'Add passkey',
  'account.passkeys.continueHint':
    'Identity confirmed. Create the passkey on this device to finish.',
  'account.passkeys.continue': 'Create passkey',
  'account.passkeys.unavailable.notConfigured':
    "Passkeys aren't available on this site. Contact your administrator.",
  'account.passkeys.unavailable.unsupported': "This browser doesn't support passkeys.",
  'account.passkeys.unavailable.insecureContext':
    'Passkeys need a secure (HTTPS) connection to this site.',
  'account.passkeys.onlyPasskeysHint':
    'Add another passkey or an authenticator app, so you can still sign in if you lose this device.',
  'account.passkeys.empty': 'No passkeys yet.',
  'account.passkeys.addedAt': 'Added {{time}}',
  'account.passkeys.lastUsed': 'Last used {{time}}',
  'account.passkeys.neverUsed': 'Never used',
  'account.passkeys.synced': 'Synced',
  'account.passkeys.notSynced': 'Not synced yet',
  'account.passkeys.deviceBound': 'This device only',
  'account.passkeys.rename': 'Rename',
  'account.passkeys.remove': 'Remove',
  'account.passkeys.nameLabel': 'Passkey name',
  'account.passkeys.removeConfirm':
    'Remove the passkey "{{name}}"? You can no longer sign in with it, and other devices will be signed out.',
  'account.passkeys.removalBlocked':
    'Your organization requires two-factor authentication. Add another passkey or an authenticator app before removing this one.',
  'account.passkeys.recoveryCodesLow':
    "Only {{count}} recovery code(s) left. Once they run out, you can't sign in if you lose your passkeys.",
  'account.passkeys.addedToast': 'Passkey added. Other devices were signed out.',
  'account.passkeys.removedToast': 'Passkey removed. Other devices were signed out.',
  'account.passkeys.renamedToast': 'Passkey renamed.',
  'account.passkeys.cancelled': 'The passkey request was cancelled.',
  'account.passkeys.alreadyRegistered': 'This device already has a passkey for your account.',
  'account.passkeys.failed': "The passkey request didn't work. Try again.",
  'account.passkeys.currentDevice': 'This device',
  'account.passkeys.loadFailed': "Couldn't load your passkeys.",
  'account.passkeys.reauthDescription': 'Confirm it is you before changing your passkeys.',
  'account.passkeys.actions': 'Actions for {{name}}',
};
//#endif
