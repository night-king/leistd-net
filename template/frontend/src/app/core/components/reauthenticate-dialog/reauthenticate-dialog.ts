import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  Injector,
  input,
  model,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { form, FormField, maxLength, required, validate } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSpinner } from '@spartan-ng/helm/spinner';

import { OtpCodeInput } from '../../../shared/components/otp-code-input/otp-code-input';
//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { PASSWORD_MAX_LENGTH } from '../../validation/password-rule';

/** 本人出示的凭证：恰好一种，调用方按各自接口的字段名带上后重试原操作。 */
export type ReauthenticationProof =
  | { readonly password: string; readonly twoFactorCode?: undefined }
  | { readonly twoFactorCode: string; readonly password?: undefined };

/** 对话框当前收哪种凭证；`signInAgain` 表示账号没有可出示的凭证，只能重新登录。 */
type Method = 'password' | 'code' | 'signInAgain';

const CODE_PATTERN = /^\d{6}$/;
const PASSWORD_INPUT_ID = 'reauthenticate-password';
const CODE_INPUT_ID = 'reauthenticate-code';
const CODE_DESCRIPTION_ID = 'reauthenticate-code-description';
const CODE_ERROR_ID = 'reauthenticate-code-error';
const CODE_REUSE_HINT_ID = 'reauthenticate-code-reuse-hint';

/**
 * 确认是本人：改邮箱、设置口令、设置或换绑两步验证、绑定或解绑外部账号、重置他人的口令或两步验证这类操作
 * 在会话不够新时由服务端要求。调用方先不带凭证提交，收到要求后打开它，再带上交出的凭证重试。
 *
 * 只负责输入、错误与提交状态，不取用户信息、不调业务接口。能出示什么由调用方按**当前用户**（管理员操作他人时
 * 是操作者本人）传入：有口令时以口令为主，已开两步验证时可改用验证码；没有口令时只收验证码，另给
 * "重新登录"的后路；两者都没有时只能重新登录。凭证对不对由服务端判定，被拒的原因由调用方经 {@link error}
 * 交回，就地显示、对话框保持打开。只交出当前所选的那一种；切换方式、关闭或成功后清掉已输入的内容。
 */
@Component({
  selector: 'app-reauthenticate-dialog',
  // 内容经浮层渲染在 body 上，宿主本身不占位：放在 gap 布局里（如账户与安全页的各节之间）不多出一道间距
  host: { class: 'hidden' },
  // prettier-ignore
  imports: [
    FormField,
    HlmButton,
    HlmInput,
    HlmSpinner,
    OtpCodeInput,
    ...HlmDialogImports,
    ...HlmFieldImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './reauthenticate-dialog.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReauthenticateDialog {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly visible = model(false);
  /** 带着凭证的重试在途：不再交出新的凭证。 */
  readonly saving = input(false);
  /** 针对具体操作的说明；留空时用通用说明。 */
  readonly description = input('');
  /** 出示凭证的人（当前用户）是否设有本地口令。 */
  readonly hasPassword = input(true);
  /** 出示凭证的人（当前用户）是否已开两步验证。 */
  readonly twoFactorEnabled = input(false);
  /** 服务端拒绝凭证的原因，显示在输入框下方；本人改动输入时清掉。 */
  readonly error = model<string | null>(null);
  /**
   * 被拒的是验证码本身（不是锁定等其他原因）：同一个码只能用一次，刚在登录或别处用过的码会被拒，
   * 此时在错误下方提示等待新码。
   */
  readonly codeRejected = input(false);
  readonly confirmed = output<ReauthenticationProof>();
  /** 本人选择重新登录（没有可出示的凭证，或用不了身份验证器）。 */
  readonly signInAgain = output<void>();

  /** 有口令且已开两步验证时，本人改用验证码。 */
  private readonly useCode = signal(false);

  protected readonly method = computed<Method>(() => {
    if (!this.hasPassword()) {
      return this.twoFactorEnabled() ? 'code' : 'signInAgain';
    }
    return this.twoFactorEnabled() && this.useCode() ? 'code' : 'password';
  });

  protected readonly canSwitch = computed(() => this.hasPassword() && this.twoFactorEnabled());

  private readonly formModel = signal({ password: '' });
  // 只做必填与长度的快速反馈；口令对不对由服务端判定，被拒的原因作为该字段的错误显示，
  // 输入框随之带上 aria-invalid，并经 aria-describedby 关联到这条错误
  protected readonly reauthForm = form(this.formModel, (path) => {
    required(path.password);
    maxLength(path.password, PASSWORD_MAX_LENGTH);
    validate(path.password, () => {
      const message = this.error();
      return message ? { kind: 'credentialRejected', message } : null;
    });
  });

  protected readonly code = signal('');
  private readonly codeInput = viewChild(OtpCodeInput);
  protected readonly codeValid = computed(() => CODE_PATTERN.test(this.code()));
  /** 最近交出的验证码：输满自动提交与回车提交同时到达时只交一次，改动输入后才允许再交。 */
  private submittedCode: string | null = null;

  protected readonly passwordInputId = PASSWORD_INPUT_ID;
  protected readonly codeInputId = CODE_INPUT_ID;
  protected readonly codeDescriptionId = CODE_DESCRIPTION_ID;
  protected readonly codeErrorId = CODE_ERROR_ID;
  protected readonly codeReuseHintId = CODE_REUSE_HINT_ID;
  protected readonly showCodeReuseHint = computed(() => !!this.error() && this.codeRejected());
  protected readonly codeDescribedBy = computed(() => {
    if (!this.error()) {
      return CODE_DESCRIPTION_ID;
    }
    const ids = `${CODE_DESCRIPTION_ID} ${CODE_ERROR_ID}`;
    return this.showCodeReuseHint() ? `${ids} ${CODE_REUSE_HINT_ID}` : ids;
  });

  constructor() {
    // 可选方式变了（本人切换，或调用方按刷新后的账号状态重新传入）：清掉另一种已输入的内容
    effect(() => {
      this.method();
      untracked(() => {
        this.clearInputs();
        this.presentError();
      });
    });
    // 凭证被拒：口令留着让本人改，验证码清空重输；错误不随清空消失，焦点回到输入框
    effect(() => {
      if (this.error()) {
        untracked(() => this.presentError());
      }
    });
  }

  /** 桥接 hlm-dialog 声明式 state 到对外 visible 契约；关闭时清掉已输入的内容与错误。 */
  protected onDialogStateChange(state: BrnDialogState): void {
    const open = state === 'open';
    this.visible.set(open);
    if (!open) {
      this.useCode.set(false);
      this.clearInputs();
      this.error.set(null);
    }
  }

  protected hide(): void {
    this.visible.set(false);
  }

  protected toggleMethod(): void {
    this.useCode.update((value) => !value);
    this.error.set(null);
    this.focus(this.method() === 'code' ? CODE_INPUT_ID : PASSWORD_INPUT_ID);
  }

  protected onPasswordInput(): void {
    this.error.set(null);
  }

  protected onCodeChange(value: string): void {
    this.code.set(value);
    this.submittedCode = null;
    // 清空不算改动：被拒后由本组件清空验证码，错误要留到本人开始重输
    if (value.length > 0) {
      this.error.set(null);
    }
  }

  protected confirm(): void {
    switch (this.method()) {
      case 'password':
        this.submitPassword();
        return;
      case 'code':
        this.submitCode();
        return;
      case 'signInAgain':
        this.signInAgain.emit();
        return;
    }
  }

  private submitPassword(): void {
    if (this.saving()) {
      return;
    }
    if (this.reauthForm().invalid()) {
      this.reauthForm().markAsTouched();
      return;
    }
    this.confirmed.emit({ password: this.formModel().password });
  }

  protected submitCode(): void {
    const code = this.code();
    if (this.saving() || !CODE_PATTERN.test(code) || code === this.submittedCode) {
      return;
    }
    this.submittedCode = code;
    this.confirmed.emit({ twoFactorCode: code });
  }

  private presentError(): void {
    if (!this.error()) {
      return;
    }
    switch (this.method()) {
      case 'password':
        this.reauthForm().markAsTouched();
        this.focus(PASSWORD_INPUT_ID);
        return;
      case 'code':
        this.clearCode();
        this.focus(CODE_INPUT_ID);
        return;
    }
  }

  private clearInputs(): void {
    this.formModel.set({ password: '' });
    this.reauthForm().reset();
    this.clearCode();
  }

  // 被拒可能与本次输入发生在同一轮变更检测之内：绑定看不出值变过，直接清空输入组件本身
  private clearCode(): void {
    this.code.set('');
    this.codeInput()?.value.set('');
    this.submittedCode = null;
  }

  // 对话框内容渲染在浮层里，按 id 找真实输入框；渲染完成后再聚焦
  private focus(id: string): void {
    afterNextRender(() => this.document.getElementById(id)?.focus(), {
      injector: this.injector,
    });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.reauthenticate.header': 'Confirm it is you',
  'common.reauthenticate.description':
    'This change affects how you or someone else signs in. Confirm it is you to continue.',
  'common.reauthenticate.passwordLabel': 'Your current password',
  'common.reauthenticate.codeLabel': 'Verification code',
  'common.reauthenticate.codeHint': 'Enter the 6-digit code from your authenticator app.',
  'common.twoFactorCodeReuseHint': 'If you just used this code, wait for a new one.',
  'common.reauthenticate.useCode': 'Use an authenticator code instead',
  'common.reauthenticate.usePassword': 'Use your password instead',
  'common.reauthenticate.cannotUseAuthenticator': "Can't use your authenticator? Sign in again",
  'common.reauthenticate.signInAgainHint':
    'Your account has no password or authenticator to confirm with. Sign in again to continue.',
  'common.reauthenticate.signInAgain': 'Sign in again',
  'common.reauthenticate.confirm': 'Confirm',
  'common.cancel': 'Cancel',
};
//#endif
