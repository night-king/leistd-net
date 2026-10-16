import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import {
  form,
  required,
  email as emailValidator,
  maxLength,
  pattern,
  FormField,
} from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleAlert, lucideCircleCheck, lucideImagePlus } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { finalize } from 'rxjs/operators';

import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { AuthService } from '../../../../core/services/auth-service';
import {
  AvatarImageRejected,
  isAvatarImageUrl,
  prepareAvatarImage,
} from '../../../../shared/utils/avatar-image';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { AccountService } from '../../services/account-service';

const PHONE_PATTERN = /^[0-9+\-()\s]{0,20}$/;

/** 当前这次邮箱验证：发出的验证码对应的挑战，以及还要等多久才能重发。 */
//#if (Email)
interface EmailChallenge {
  challengeId: string;
  email: string;
}

//#endif
/**
 * 个人设置 ·「个人资料」面板：头像、用户名、邮箱（含验证）、显示名、手机。
 *
 * - 表单跟着当前用户走：进面板、保存成功后（服务端返回的新值写回当前用户）都按当前用户重填；
 *   「撤销修改」同样回到当前用户的值。
 * - 头像选完即生效，不跟表单一起保存：它有自己的接口，也不该被"撤销修改"撤回。
 * - 邮箱验证针对的是**已保存**的邮箱；表单里改了还没保存时，先提示保存后再验证。
 */
@Component({
  selector: 'app-profile-panel',
  standalone: true,
  // prettier-ignore
  imports: [
    FormField,
    NgIcon,
    HlmButton,
    HlmSpinner,
    HlmInput,
    HlmBadge,
    ...HlmFieldImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideCircleAlert, lucideCircleCheck, lucideImagePlus })],
  templateUrl: './profile-panel.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProfilePanel {
  private readonly authService = inject(AuthService);
  private readonly accountService = inject(AccountService);
  private readonly destroyRef = inject(DestroyRef);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly saving = signal(false);
  readonly user = computed(() => this.authService.currentUser());

  /** 角色徽章直接展示后端返回的角色名。 */
  readonly roleLabels = computed(() => this.authService.currentUser()?.roles ?? []);

  /** 正在上传的头像（处理好的 data URL）；上传期间先显示它，完成后换成服务端给的地址。 */
  private readonly pendingAvatar = signal<string | null>(null);
  readonly avatarBusy = signal(false);
  readonly avatarPreview = computed(() => this.pendingAvatar() ?? this.user()?.avatar ?? '');
  readonly hasAvatarImage = computed(() => isAvatarImageUrl(this.avatarPreview()));

  protected readonly formModel = signal({
    username: '',
    email: '',
    displayName: '',
    phoneNumber: '',
  });

  /** 都没有时为空串，模板里回落到"访客"文案。 */
  readonly displayName = computed(
    () =>
      this.formModel().displayName.trim() ||
      this.user()?.displayName ||
      this.user()?.username ||
      '',
  );

  /** 表单里的邮箱与已保存的不同：验证只能对已保存的邮箱做。 */
  readonly emailEdited = computed(
    () => this.formModel().email.trim().toLowerCase() !== (this.user()?.email ?? '').toLowerCase(),
  );

  /**
   * 部署能不能发邮箱验证码（验证码摘要密钥是否已配置）。取回之前按"能"处理：
   * 多数部署都配了，先藏起按钮再冒出来反而闪一下；真不能时发送端也会给出明确原因。
   */
//#if (Email)
  readonly emailVerificationAvailable = signal(true);

  readonly emailChallenge = signal<EmailChallenge | null>(null);
  readonly emailCode = signal('');
  readonly sendingCode = signal(false);
  readonly confirmingCode = signal(false);
  /** 距离可以重发还剩几秒；0 表示可以发。 */
  readonly resendSeconds = signal(0);
  private resendTimer: ReturnType<typeof setInterval> | undefined;

//#endif
  readonly profileForm = form(this.formModel, (path) => {
    required(path.username);
    pattern(path.username, /^[a-zA-Z0-9_]{3,64}$/, { error: { kind: 'usernamePattern' } });
    required(path.email);
    emailValidator(path.email);
    maxLength(path.email, 256);
    maxLength(path.displayName, 128);
    maxLength(path.phoneNumber, 20);
    pattern(path.phoneNumber, PHONE_PATTERN, { error: { kind: 'phonePattern' } });
  });

  constructor() {
    // 当前用户一变（首次取回、保存成功后写回）就按它重填；表单模型不参与追踪，
    // 否则每敲一个字都会触发重填，把输入抹掉。
    effect(() => {
      this.user();
      untracked(() => this.reset());
    });
//#if (Email)
    this.destroyRef.onDestroy(() => clearInterval(this.resendTimer));
    this.accountService
      .getSecurityConfig()
      .subscribe((config) =>
        this.emailVerificationAvailable.set(config.emailVerificationAvailable),
      );
//#endif
  }

  /** 按当前用户回填表单，丢掉未保存的修改。 */
  reset(): void {
    const user = this.user();
    this.formModel.set({
      username: user?.username ?? '',
      email: user?.email ?? '',
      displayName: user?.displayName ?? '',
      phoneNumber: user?.phoneNumber ?? '',
    });
//#if (Email)
    // 已保存的邮箱变了（改邮箱后保存），手里那个挑战是发给旧地址的，作废
    if (this.emailChallenge()?.email !== user?.email) {
      this.emailChallenge.set(null);
      this.emailCode.set('');
    }
//#endif
  }

  /** 选完图片即处理并上传：裁成正方形、缩到 256，失败原因就地说清。 */
  async onAvatarSelect(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // 允许再次选择同一文件时仍触发 change 事件
    input.value = '';
    if (!file) {
      return;
    }

    let dataUrl: string;
    try {
      dataUrl = await prepareAvatarImage(file);
    } catch (error: unknown) {
      const reason = error instanceof AvatarImageRejected ? error.reason : 'decode';
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('account.profile.avatarRejectedSummary'), {
        description: this.transloco.translate(`account.profile.avatarRejected.${reason}`),
      });
      //#else
      toast.error(this.t('account.profile.avatarRejectedSummary'), {
        description: this.t(`account.profile.avatarRejected.${reason}`),
      });
      //#endif
      return;
    }

    this.pendingAvatar.set(dataUrl);
    this.applyAvatar(dataUrl, 'account.profile.avatarUpdated');
  }

  removeAvatar(): void {
    this.applyAvatar(null, 'account.profile.avatarRemoved');
  }

  private applyAvatar(avatar: string | null, successKey: string): void {
    this.avatarBusy.set(true);
    this.accountService
      .setAvatar({ avatar })
      .pipe(
        finalize(() => {
          this.avatarBusy.set(false);
          this.pendingAvatar.set(null);
        }),
      )
      .subscribe({
        next: (user) => {
          this.authService.setCurrentUser(user);
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate(successKey));
          //#else
          toast.success(this.t(successKey));
          //#endif
        },
        error: (error) => this.showRequestError(error),
      });
  }

//#if (Email)
  /** 给已保存的邮箱发验证码，并开始重发倒计时。 */
  sendEmailCode(): void {
    const email = this.user()?.email;
    if (!email) {
      return;
    }

    this.sendingCode.set(true);
    this.accountService
      .sendCurrentEmailCode()
      .pipe(finalize(() => this.sendingCode.set(false)))
      .subscribe({
        next: (challenge) => {
          this.emailChallenge.set({ challengeId: challenge.challengeId, email });
          this.emailCode.set('');
          this.startResendCountdown(challenge.retryAfterSeconds);
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('account.profile.codeSent', { email }));
          //#else
          toast.success(this.t('account.profile.codeSent', { email }));
          //#endif
        },
        error: (error) => this.showRequestError(error),
      });
  }

  confirmEmailCode(): void {
    const challenge = this.emailChallenge();
    const code = this.emailCode().trim();
    if (!challenge || code.length === 0) {
      return;
    }

    this.confirmingCode.set(true);
    this.accountService
      .confirmCurrentEmail({ challengeId: challenge.challengeId, code })
      .pipe(finalize(() => this.confirmingCode.set(false)))
      .subscribe({
        next: (user) => {
          this.authService.setCurrentUser(user);
          this.emailChallenge.set(null);
          this.emailCode.set('');
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('account.profile.emailVerifiedSuccess'));
          //#else
          toast.success(this.t('account.profile.emailVerifiedSuccess'));
          //#endif
        },
        error: (error) => this.showRequestError(error),
      });
  }

  onEmailCodeInput(event: Event): void {
    this.emailCode.set((event.target as HTMLInputElement).value);
  }

  private startResendCountdown(seconds: number): void {
    clearInterval(this.resendTimer);
    this.resendSeconds.set(Math.max(0, Math.ceil(seconds)));
    this.resendTimer = setInterval(() => {
      const next = this.resendSeconds() - 1;
      this.resendSeconds.set(Math.max(0, next));
      if (next <= 0) {
        clearInterval(this.resendTimer);
      }
    }, 1000);
  }

//#endif
  onSubmit(): void {
    if (this.profileForm().invalid()) {
      this.profileForm().markAsTouched();
      return;
    }

    this.saving.set(true);
    const { username, email, displayName, phoneNumber } = this.formModel();

    this.accountService
      .updateCurrentUser({
        username: username.trim(),
        email: email.trim(),
        displayName: displayName.trim() || undefined,
        phoneNumber: phoneNumber.trim() || undefined,
      })
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: (user) => {
          this.authService.setCurrentUser(user);
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('common.success'), {
            description: this.transloco.translate('account.profile.updateSuccess'),
          });
          //#else
          toast.success(this.t('common.success'), {
            description: this.t('account.profile.updateSuccess'),
          });
          //#endif
        },
        error: (error) => this.showRequestError(error),
      });
  }

  private showRequestError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error(this.t('common.requestError'), { description: applicationErrorMessage(error) });
    //#endif
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.success': 'Success',
  'common.requestError': 'Request error',
  'account.profile.guestUser': 'Guest user',
  'account.profile.updateSuccess': 'Profile updated',
  'account.profile.avatarUpdated': 'Avatar updated',
  'account.profile.avatarRemoved': 'Avatar removed',
  'account.profile.avatarRejectedSummary': 'Cannot use this image',
  'account.profile.avatarRejected.type': 'Please choose a PNG, JPG or WebP image.',
  'account.profile.avatarRejected.size': 'The image cannot exceed 10 MB.',
  'account.profile.avatarRejected.decode': 'This image could not be read. Try another one.',
  'account.profile.codeSent': 'A verification code was sent to {{email}}',
  'account.profile.emailVerifiedSuccess': 'Email address verified',
  'common.uploadAvatar': 'Upload avatar',
  'account.profile.superAdminTag': 'Super Admin',
  'account.profile.avatarHint': 'PNG, JPG or WebP; cropped to a square',
  'account.profile.removeAvatar': 'Remove avatar',
  'account.profile.username': 'Username',
  'account.profile.usernamePlaceholder': 'Please enter a username',
  'account.profile.email': 'Email',
  'account.profile.emailPlaceholder': 'Please enter an email',
  'account.profile.emailChangeHint': 'Save first, then verify the new email address.',
  'account.profile.emailVerified': 'Verified',
  'account.profile.emailUnverified': 'Not verified',
  'account.profile.emailVerificationUnavailable':
    'Email verification is not set up on this deployment yet.',
  'account.profile.codeLabel': 'Verification code',
  'account.profile.codePlaceholder': '6-digit code from the email',
  'account.profile.confirmCode': 'Verify',
  'account.profile.resendIn': 'Resend in {{seconds}}s',
  'account.profile.resendCode': 'Resend code',
  'account.profile.sendCode': 'Send verification code',
  'account.profile.displayName': 'Display name',
  'account.profile.displayNamePlaceholder': 'Enter a display name',
  'account.profile.phoneNumber': 'Phone Number',
  'account.profile.phoneNumberPlaceholder': 'Please enter a phone number',
  'account.profile.discardChanges': 'Discard changes',
  'common.save': 'Save',
};
//#endif
