import { inject, Signal, signal } from '@angular/core';
import { toast } from '@spartan-ng/brain/sonner';
import { lastValueFrom } from 'rxjs';

import { ReauthenticationProof } from '../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import {
  injectReauthentication,
  Reauthentication,
} from '../../../core/components/reauthenticate-dialog/reauthentication';
import { currentPasskeyDeviceName } from '../../../core/webauthn/passkey-device-name';
import { createPasskey } from '../../../core/webauthn/webauthn';
import { classifyWebAuthnError, isWebAuthnError } from '../../../core/webauthn/webauthn-error';
import { WebAuthnJson } from '../../../shared/dtos/account-security.dto';
import { PasskeyRegistrationOutputDto } from '../dtos/account.dto';
import { AccountService } from '../services/account-service';

/** 登记一把通行密钥的回调与文案；文案由调用方按本地化与否取出。 */
export interface PasskeyRegistrationOptions {
  /** 取文案：`account.passkeys.currentDevice` 与浏览器失败的三条提示。 */
  readonly text: (key: string) => string;
  /** 登记成功；`recoveryCodes` 只在它是第一个独立因子时有值。 */
  readonly registered: (result: PasskeyRegistrationOutputDto) => void;
  /** 与确认身份无关的服务端失败（浏览器这一侧的失败已按归类提示）。 */
  readonly failed: (error: unknown) => void;
  /** 确认身份之后停下来等本人再点一次（调用方可把焦点移到"创建通行密钥"上）。 */
  readonly awaitingGesture?: () => void;
}

/** 一个调用方的登记状态；由 {@link injectPasskeyRegistration} 在组件的注入上下文里创建。 */
export interface PasskeyRegistration {
  /** 确认身份的状态，调用方把它接到自己的确认身份对话框上。 */
  readonly reauth: Reauthentication;
  /** 登记在途（取选项、浏览器创建、提交）。 */
  readonly adding: Signal<boolean>;
  /** 确认身份已通过、创建选项已到手，等本人再点一次。 */
  readonly awaitingGesture: Signal<boolean>;
  /** 本人点"添加 / 创建通行密钥"。 */
  start(): void;
  /** 确认身份之后本人再点一次：在这次点击里调起浏览器。 */
  continue(): void;
  /** 放弃等待中的这一次。 */
  cancel(): void;
  /** 确认身份对话框开关变化；本人关掉时放弃这一次。 */
  setReauthVisible(visible: boolean): void;
}

/**
 * 登记通行密钥的共同流程（个人设置的通行密钥一节、强制启用两步验证页共用）：先不带凭证取创建选项，
 * 服务端要求确认身份时打开对话框；确认之后停下来等本人再点一次，因为浏览器要求创建发生在用户手势里，
 * 关掉对话框的那次点击已经用掉了。会话刚认证过时这次点击直接调起浏览器。默认名称取自设备与浏览器。
 */
export function injectPasskeyRegistration(
  options: PasskeyRegistrationOptions,
): PasskeyRegistration {
  const accountService = inject(AccountService);
  const reauth = injectReauthentication();
  const adding = signal(false);
  const pendingOptions = signal<WebAuthnJson | null>(null);
  const awaitingGesture = signal(false);

  const create = async (creationOptions: WebAuthnJson): Promise<void> => {
    try {
      const credential = await createPasskey(creationOptions);
      const name = currentPasskeyDeviceName() ?? options.text('account.passkeys.currentDevice');
      const result = await lastValueFrom(
        accountService.completePasskeyRegistration({ credential, name }),
      );
      options.registered(result);
    } catch (error) {
      if (!notifyWebAuthnFailure(error, options.text)) {
        options.failed(error);
      }
    } finally {
      adding.set(false);
    }
  };

  const begin = (proof?: ReauthenticationProof): void => {
    accountService
      .beginPasskeyRegistration({
        password: proof?.password,
        twoFactorCode: proof?.twoFactorCode,
        passkey: proof?.passkey,
      })
      .subscribe({
        next: ({ options: creationOptions }) => {
          if (proof === undefined) {
            void create(creationOptions);
            return;
          }
          reauth.complete();
          adding.set(false);
          pendingOptions.set(creationOptions);
          awaitingGesture.set(true);
          options.awaitingGesture?.();
        },
        error: (error) => {
          if (reauth.handleError(error, proof, (next) => begin(next))) {
            return;
          }
          adding.set(false);
          options.failed(error);
        },
      });
  };

  const cancel = (): void => {
    pendingOptions.set(null);
    awaitingGesture.set(false);
  };

  return {
    reauth,
    adding: adding.asReadonly(),
    awaitingGesture: awaitingGesture.asReadonly(),

    start() {
      if (adding()) {
        return;
      }
      cancel();
      adding.set(true);
      begin();
    },

    continue() {
      const creationOptions = pendingOptions();
      if (!creationOptions || adding()) {
        return;
      }
      cancel();
      adding.set(true);
      void create(creationOptions);
    },

    cancel,

    setReauthVisible(visible) {
      const saving = reauth.saving();
      reauth.setVisible(visible);
      if (!visible && !saving) {
        adding.set(false);
      }
    },
  };
}

/**
 * 浏览器这一侧的失败按归类提示（取消只是中性提示），不显示浏览器原文；返回 `false` 表示不是浏览器的失败，
 * 由调用方按服务端的原因处理。
 */
export function notifyWebAuthnFailure(error: unknown, text: (key: string) => string): boolean {
  if (!isWebAuthnError(error)) {
    return false;
  }
  switch (classifyWebAuthnError(error)) {
    case 'cancelled':
      toast.info(text('account.passkeys.cancelled'));
      break;
    case 'alreadyRegistered':
      toast.warning(text('account.passkeys.alreadyRegistered'));
      break;
    default:
      toast.error(text('account.passkeys.failed'));
  }
  return true;
}
