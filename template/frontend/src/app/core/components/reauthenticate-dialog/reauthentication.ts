import { computed, inject, Signal, signal, WritableSignal } from '@angular/core';
import { Router } from '@angular/router';
import { lastValueFrom } from 'rxjs';

import { ReauthenticationProof } from './reauthenticate-dialog';
import { API_ERROR_CODES } from '../../errors/api-error-codes';
import { ApplicationHttpError, applicationErrorMessage } from '../../errors/application-http-error';
import { AuthService } from '../../services/auth-service';
import { getPasskey } from '../../webauthn/webauthn';
import {
  classifyWebAuthnError,
  isWebAuthnError,
  WebAuthnFailure,
} from '../../webauthn/webauthn-error';

/**
 * 带着凭证重试仍被拒、要留在对话框里就地说明的错误：口令或验证码不对、通行密钥没通过或这次仪式已失效、
 * 方式本账号不具备（含部署没配通行密钥）、再认证失败过多被临时锁定。其余失败与凭证无关（如邮箱已被占用），
 * 关掉对话框交还调用方。
 */
const PROOF_REJECTIONS: ReadonlySet<string> = new Set([
  API_ERROR_CODES.currentPasswordIncorrect,
  API_ERROR_CODES.twoFactorCodeInvalid,
  API_ERROR_CODES.passkeyVerificationFailed,
  API_ERROR_CODES.passkeyCeremonyExpired,
  API_ERROR_CODES.passkeyUnavailable,
  API_ERROR_CODES.reauthenticationMethodUnavailable,
  API_ERROR_CODES.userTemporarilyLockedOut,
]);

/** 本账号此刻不能用通行密钥确认：刷新当前用户，对话框按新状态重新给出可选方式。 */
const PASSKEY_UNUSABLE: ReadonlySet<string> = new Set([
  API_ERROR_CODES.reauthenticationMethodUnavailable,
  API_ERROR_CODES.passkeyUnavailable,
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
  /** 就地显示的原因是验证码不对：可能是刚用过的码（同一个码只能用一次），对话框据此提示等待新码。 */
  readonly codeRejected: Signal<boolean>;
  /** 出示凭证的是当前用户（管理员操作他人时是操作者本人），不是操作对象。 */
  readonly hasPassword: Signal<boolean>;
  readonly twoFactorEnabled: Signal<boolean>;
  readonly hasPasskeys: Signal<boolean>;
  /**
   * 通行密钥在浏览器这一侧没成（本人取消、认证器失败）：对话框按归类给出本地化说明，不显示浏览器原文；
   * 本人改动选择或再试时清掉。
   */
  readonly passkeyFailure: WritableSignal<WebAuthnFailure | null>;

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
  /**
   * 本人选择用通行密钥确认（须在点击里调用，浏览器要求用户手势）：取断言选项、调起浏览器，
   * 断言作为 `passkey` 凭证重试挂起的操作。
   */
  confirmWithPasskey(): void;
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
  const codeRejected = signal(false);
  const hasPassword = computed(() => authService.currentUser()?.hasPassword ?? true);
  const twoFactorEnabled = computed(() => authService.currentUser()?.isTwoFactorEnabled ?? false);
  const hasPasskeys = computed(() => authService.currentUser()?.hasPasskeys ?? false);
  const passkeyFailure = signal<WebAuthnFailure | null>(null);
  let pending: ((proof: ReauthenticationProof) => void) | null = null;

  // 账号状态与服务端不一致（别处设了口令、停用了两步验证）：重新取当前用户，对话框随之换成可用的方式
  const refreshCurrentUser = (): void => {
    authService.loadUser().subscribe({ error: () => undefined });
  };

  const clearError = (): void => {
    error.set(null);
    codeRejected.set(false);
    passkeyFailure.set(null);
  };

  const complete = (): void => {
    pending = null;
    saving.set(false);
    clearError();
    visible.set(false);
  };

  return {
    visible: visible.asReadonly(),
    saving: saving.asReadonly(),
    error,
    codeRejected: codeRejected.asReadonly(),
    hasPassword,
    twoFactorEnabled,
    hasPasskeys,
    passkeyFailure,

    handleError(failure, proof, retry) {
      saving.set(false);
      const code = failure instanceof ApplicationHttpError ? failure.code : undefined;

      if (
        code === API_ERROR_CODES.reauthenticationRequired ||
        code === API_ERROR_CODES.signInAgainRequired
      ) {
        // 只有口令、身份验证器与通行密钥都没有才会要求重新登录；本地状态与之不符，或还没取过当前用户
        // （回调页整页加载时不取）时以服务端为准
        const signInAgain = code === API_ERROR_CODES.signInAgainRequired;
        if (
          authService.currentUser() === null ||
          signInAgain !== (!hasPassword() && !twoFactorEnabled() && !hasPasskeys())
        ) {
          refreshCurrentUser();
        }
        pending = retry;
        clearError();
        visible.set(true);
        return true;
      }

      if (proof !== undefined && visible() && code !== undefined && PROOF_REJECTIONS.has(code)) {
        if (PASSKEY_UNUSABLE.has(code)) {
          refreshCurrentUser();
        }
        error.set(applicationErrorMessage(failure));
        codeRejected.set(code === API_ERROR_CODES.twoFactorCodeInvalid);
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
      clearError();
      pending(proof);
    },

    confirmWithPasskey() {
      if (!pending || saving()) {
        return;
      }
      const retry = pending;
      saving.set(true);
      clearError();
      void (async () => {
        let passkey;
        try {
          const { options } = await lastValueFrom(authService.beginPasskeyReauthentication());
          passkey = await getPasskey(options);
        } catch (failure) {
          saving.set(false);
          // 对话框已关闭或换了一次操作：这次的结果作废
          if (pending !== retry) {
            return;
          }
          if (isWebAuthnError(failure)) {
            passkeyFailure.set(classifyWebAuthnError(failure));
            return;
          }
          const failureCode = failure instanceof ApplicationHttpError ? failure.code : undefined;
          if (failureCode !== undefined && PASSKEY_UNUSABLE.has(failureCode)) {
            refreshCurrentUser();
          }
          error.set(applicationErrorMessage(failure));
          return;
        }
        if (pending !== retry) {
          saving.set(false);
          return;
        }
        retry({ passkey });
      })();
    },

    complete,

    setVisible(value) {
      visible.set(value);
      if (!value) {
        pending = null;
        saving.set(false);
        clearError();
      }
    },

    signInAgain(returnUrl = router.url) {
      pending = null;
      authService.signInAgain(returnUrl);
    },
  };
}
