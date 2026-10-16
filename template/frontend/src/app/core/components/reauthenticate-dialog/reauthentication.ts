import { computed, inject, Signal, signal, WritableSignal } from '@angular/core';
import { Router } from '@angular/router';

import { ReauthenticationProof } from './reauthenticate-dialog';
import { API_ERROR_CODES } from '../../errors/api-error-codes';
import { ApplicationHttpError, applicationErrorMessage } from '../../errors/application-http-error';
import { AuthService } from '../../services/auth-service';

/**
 * 带着凭证重试仍被拒、要留在对话框里就地说明的错误：口令或验证码不对、方式本账号不具备、
 * 再认证失败过多被临时锁定。其余失败与凭证无关（如邮箱已被占用），关掉对话框交还调用方。
 */
const PROOF_REJECTIONS: ReadonlySet<string> = new Set([
  API_ERROR_CODES.currentPasswordIncorrect,
  API_ERROR_CODES.twoFactorCodeInvalid,
  API_ERROR_CODES.reauthenticationMethodUnavailable,
  API_ERROR_CODES.userTemporarilyLockedOut,
]);

/**
 * 一个调用方的确认身份状态：对话框的开关、在途与就地错误，以及等凭证重试的那一次操作。
 * 由 {@link injectReauthentication} 在调用方组件里创建，随组件销毁。
 */
export interface Reauthentication {
  readonly visible: Signal<boolean>;
  readonly saving: Signal<boolean>;
  /** 对话框里就地显示的原因；对话框在本人改动输入时清掉它。 */
  readonly error: WritableSignal<string | null>;
  /** 出示凭证的是当前用户（管理员操作他人时是操作者本人），不是操作对象。 */
  readonly hasPassword: Signal<boolean>;
  readonly twoFactorEnabled: Signal<boolean>;

  /**
   * 处理一次失败。服务端要求确认身份时打开对话框并记下 `retry`；带着凭证仍被拒时把原因就地放进对话框
   * （方式不具备时刷新当前用户，对话框按新状态重新给出可选方式）。返回 `false` 表示与确认身份无关，
   * 由调用方照常处理；此时对话框已关闭。
   *
   * @param proof 这次请求带的凭证；第一次不带凭证的提交传 `undefined`。
   * @param retry 带上本人交出的凭证重试原操作。
   */
  handleError(
    error: unknown,
    proof: ReauthenticationProof | undefined,
    retry: (proof: ReauthenticationProof) => void,
  ): boolean;
  /** 对话框交出凭证：带上它重试挂起的操作。 */
  confirm(proof: ReauthenticationProof): void;
  /** 操作成功：关闭对话框，清掉挂起的重试。 */
  complete(): void;
  /** 对话框开关变化；本人关掉时放弃挂起的操作。 */
  setVisible(visible: boolean): void;
  /** 本人选择重新登录：退出后回到登录页，登录成功回到 `returnUrl`（默认当前页面）。 */
  signInAgain(returnUrl?: string): void;
}

/**
 * 确认身份的调用约定：先不带凭证提交原操作，服务端返回 `Auth:ReauthenticationRequired` 或
 * `Auth:SignInAgainRequired` 时才打开对话框，带上本人交出的凭证重试。账号状态只决定对话框给出哪种输入，
 * 是否需要确认、凭证对不对都由服务端判定。
 */
export function injectReauthentication(): Reauthentication {
  const authService = inject(AuthService);
  const router = inject(Router);

  const visible = signal(false);
  const saving = signal(false);
  const error = signal<string | null>(null);
  const hasPassword = computed(() => authService.currentUser()?.hasPassword ?? true);
  const twoFactorEnabled = computed(() => authService.currentUser()?.isTwoFactorEnabled ?? false);
  let pending: ((proof: ReauthenticationProof) => void) | null = null;

  // 账号状态与服务端不一致（别处设了口令、停用了两步验证）：重新取当前用户，对话框随之换成可用的方式
  const refreshCurrentUser = (): void => {
    authService.loadUser().subscribe({ error: () => undefined });
  };

  const complete = (): void => {
    pending = null;
    saving.set(false);
    error.set(null);
    visible.set(false);
  };

  return {
    visible: visible.asReadonly(),
    saving: saving.asReadonly(),
    error,
    hasPassword,
    twoFactorEnabled,

    handleError(failure, proof, retry) {
      saving.set(false);
      const code = failure instanceof ApplicationHttpError ? failure.code : undefined;

      if (
        code === API_ERROR_CODES.reauthenticationRequired ||
        code === API_ERROR_CODES.signInAgainRequired
      ) {
        // 只有既无口令又没开两步验证才会要求重新登录；本地状态与之不符，或还没取过当前用户
        // （回调页整页加载时不取）时以服务端为准
        const signInAgain = code === API_ERROR_CODES.signInAgainRequired;
        if (
          authService.currentUser() === null ||
          signInAgain !== (!hasPassword() && !twoFactorEnabled())
        ) {
          refreshCurrentUser();
        }
        pending = retry;
        error.set(null);
        visible.set(true);
        return true;
      }

      if (proof !== undefined && visible() && code !== undefined && PROOF_REJECTIONS.has(code)) {
        if (code === API_ERROR_CODES.reauthenticationMethodUnavailable) {
          refreshCurrentUser();
        }
        error.set(applicationErrorMessage(failure));
        return true;
      }

      complete();
      return false;
    },

    confirm(proof) {
      if (!pending || saving()) {
        return;
      }
      saving.set(true);
      error.set(null);
      pending(proof);
    },

    complete,

    setVisible(value) {
      visible.set(value);
      if (!value) {
        pending = null;
        saving.set(false);
        error.set(null);
      }
    },

    signInAgain(returnUrl = router.url) {
      pending = null;
      authService.signInAgain(returnUrl);
    },
  };
}
