//#if (IncludeLocalization)
import { translateObjectSignal } from '@jsverse/transloco';

import { textAt } from '../../../../../../core/i18n/translation-text';
//#endif
import { OperationRecordOutputDto } from '../../../../dtos/operation-record.dto';

/** 「操作内容」那句话：`key` 非空时模板经 t 取词条并填 `params`，否则直接显示 `text`。 */
export interface ActionSentence {
  key: string | null;
  params: Record<string, string>;
  text: string;
}
//#if (!IncludeLocalization)

/**
 * 不启用多语言时的英文句子表，与 `public/i18n/en.json` 的 `operationRecords.actions` 必须同步改：
 * 该场景不生成词条文件，不内联就只能显示裸动作码（`scripts/check-operation-action-i18n.py` 核对两者）。
 */
const ACTION_SENTENCES: Record<string, (target: string) => string> = {
  'user.created': (t) => `Created user ${t}`,
  'user.updated': (t) => `Updated user ${t}`,
  'user.deleted': (t) => `Deleted user ${t}`,
  'user.enabled': (t) => `Enabled user ${t}`,
  'user.disabled': (t) => `Disabled user ${t}`,
  'user.password-reset': (t) => `Reset the password of user ${t}`,
  'user.unlocked': (t) => `Unlocked user ${t}`,
  'user.two-factor-reset': (t) => `Reset two-factor authentication for user ${t}`,
  'user.roles-replaced': (t) => `Changed roles for user ${t}`,
  'role.created': (t) => `Created role ${t}`,
  'role.updated': (t) => `Updated role ${t}`,
  'role.deleted': (t) => `Deleted role ${t}`,
  //#if (OpenIddictServer)
  'open-application.created': (t) => `Created open application ${t}`,
  'open-application.updated': (t) => `Updated open application ${t}`,
  'open-application.deleted': (t) => `Deleted open application ${t}`,
  'open-application.secret-reset': (t) => `Reset the secret of open application ${t}`,
  //#endif
  //#if (RemoteTokenAuth)
  'resource.admin-granted': (t) => `Added ${t} to the administrator role`,
  'resource.admin-restored': (t) => `Restored administrator access for ${t}`,
  //#endif
  'permission-grants.replaced': (t) => `Changed permissions for ${t}`,
  'tenant.created': (t) => `Created tenant ${t}`,
  'tenant.updated': (t) => `Updated tenant ${t}`,
  'tenant.activation-changed': (t) => `Changed activation of tenant ${t}`,
  'tenant.deleted': (t) => `Deleted tenant ${t}`,
  'tenant.connection-registered': (t) => `Registered a database connection for tenant ${t}`,
  'tenant.connection-changed': (t) => `Changed the database connection of tenant ${t}`,
  'tenant.connection-removed': (t) => `Removed the database connection of tenant ${t}`,
  //#if (LocalIdentity && IncludeMultiTenancy)
  'tenant.admin-restored': (t) => `Restored tenant administrator ${t}`,
  //#endif
  'setting.changed': (t) => `Changed setting ${t}`,
  'setting.test-email-sent': () => 'Sent a test email',
  'operation-records.exported': () => 'Exported operation records',
  'auth.login.succeeded': () => 'Signed in',
  'auth.login.failed': (t) => `Failed to sign in (${t})`,
  'auth.locked-out': (t) => `Account ${t} was locked after repeated failed sign-ins`,
  'auth.password.changed': () => 'Changed their own password',
  //#if (Email)
  'auth.password.recovered': (t) => `Account ${t} reset its password with an email code`,
  //#endif
  'auth.avatar.changed': () => 'Changed their avatar',
  'auth.email.changed': () => 'Changed their email address',
  'auth.email.verified': () => 'Verified their email address',
  'auth.session.revoked': (t) => `Signed out a device (${t})`,
  'auth.sessions.others-revoked': () => 'Signed out all other devices',
  'auth.two-factor.enabled': () => 'Turned on two-factor authentication',
  'auth.two-factor.authenticator-replaced': () => 'Replaced their authenticator app',
  'auth.two-factor.disabled': () => 'Turned off two-factor authentication',
  'auth.two-factor.recovery-codes-regenerated': () => 'Regenerated recovery codes',
  'auth.two-factor.recovery-code-used': (t) => `Account ${t} signed in with a recovery code`,
  //#if (Email)
  'auth.two-factor.email-enabled': () => 'Turned on email verification codes',
  'auth.two-factor.email-disabled': () => 'Turned off email verification codes',
  //#endif
  //#if (ExternalLogin)
  'auth.external-login.linked': (t) => `Linked external account ${t}`,
  'auth.external-login.unlinked': (t) => `Unlinked external account ${t}`,
  'auth.password.set': () => 'Set their own password',
  //#endif
  'auth.registered': (t) => `Registered account ${t}`,
  'impersonation.started': (t) => `Started acting as ${t}`,
  'impersonation.ended': () => 'Ended impersonation',
  'tenant.impersonation-started': (t) => `Signed in as the administrator of tenant ${t}`,
  'tenant.impersonation-ended': (t) => `Ended impersonation of tenant ${t}`,
  'auth.token.issued': () => 'Issued an access token',
};

/** 无目标时的变体（目标记为 `-`）：创建类端点在授权阶段被拒，以及本来就不带目标的动作。 */
const ACTION_SENTENCES_NO_TARGET: Record<string, string> = {
  'user.created': 'Created a user',
  'role.created': 'Created a role',
  //#if (OpenIddictServer)
  'open-application.created': 'Created an open application',
  //#endif
  'setting.test-email-sent': 'Sent a test email',
};
//#endif

/**
 * 取「操作内容」句子的函数，操作记录表格与仪表盘的最近操作共用。须在注入上下文中调用；返回的函数读取
 * 词条信号，放进 `computed` 里随语言切换重算。整句进语言包，不拼片段（各语言语序不同）。目标降级：
 * 名称 → 截断的标识 → 无目标（`-`）走 `actionsNoTarget`；未登记的动作码显示裸码，不造假句子。
 */
export function injectOperationActionSentence(): (
  record: OperationRecordOutputDto,
) => ActionSentence {
  //#if (IncludeLocalization)
  // 动作句子模板整段取成对象，只用来判断动作码登记了没有：句子本身在模板里经 t 带参数取。
  const actionTexts = translateObjectSignal(
    'operationRecords.actions',
    {},
    { scope: 'operationRecords' },
  );
  const actionNoTargetTexts = translateObjectSignal(
    'operationRecords.actionsNoTarget',
    {},
    { scope: 'operationRecords' },
  );

  return (record): ActionSentence => {
    if (record.targetId === '-' && textAt(actionNoTargetTexts(), record.action) !== undefined) {
      return { key: `operationRecords.actionsNoTarget.${record.action}`, params: {}, text: '' };
    }

    return textAt(actionTexts(), record.action) === undefined
      ? { key: null, params: {}, text: record.action }
      : {
          key: `operationRecords.actions.${record.action}`,
          params: { target: operationTargetDisplay(record) },
          text: '',
        };
  };
  //#else
  return (record): ActionSentence => {
    if (record.targetId === '-') {
      const noTarget = ACTION_SENTENCES_NO_TARGET[record.action];
      if (noTarget) {
        return { key: null, params: {}, text: noTarget };
      }
    }

    const template = ACTION_SENTENCES[record.action];
    const text = template ? template(operationTargetDisplay(record)) : record.action;
    return { key: null, params: {}, text };
  };
  //#endif
}

/** 目标的显示形态：无名称（如授权阶段被拒）时退到截断的标识，避免 36 位 GUID 撑开整行。 */
export function operationTargetDisplay(record: OperationRecordOutputDto): string {
  if (record.targetName) {
    return record.targetName;
  }
  return record.targetId.length > 12 ? `${record.targetId.slice(0, 8)}…` : record.targetId;
}
