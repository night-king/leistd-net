import { OperationRecordOutputDto } from '../../../../dtos/operation-record.dto';
//#if (!IncludeLocalization)

/**
 * 失败原因的英文文案，键是失败码。不启用多语言时后端 `failureMessage` 恒为空，须与后端
 * `Api/Resources/en.json` 同步：有 `{码}:Record` 的取它，否则取码本身。`{name}` 占位符用
 * `failureData` 填，键名区分大小写。
 */
const FAILURE_REASONS: Record<string, string> = {
  'Auth:InvalidCredentials':
    'Incorrect username or password (failed attempts in the last {windowMinutes} minutes: {attempts})',
  'Auth:UserTemporarilyLockedOut':
    'Locked out for {minutes} minutes after {maxFailedAttempts} failed sign-in attempts',
  // 框架唯一自产的失败码（RecordDeniedOperationAsync 的默认原因），没有这条会显示裸码
  'Error:Forbidden': 'Not allowed to perform this action.',
  // 再认证失败：改口令、停用两步验证、重发恢复码这三处
  'Security:CurrentPasswordIncorrect': 'The current password is incorrect.',
  'Auth:TwoFactorCodeInvalid': 'The verification code is incorrect.',
};
//#endif

/**
 * 操作人；都缺失时为 undefined，由调用方显示"匿名"。宿主未下发 name claim 时只有标识。
 * `actorIsTarget`（服务端判定）为真时回落到目标名，前端不按动作码前缀猜。
 */
export function operationRecordActor(record: OperationRecordOutputDto): string | undefined {
  const fromTarget = record.actorIsTarget ? record.targetName : undefined;
  return record.actorName ?? record.actorId ?? fromTarget;
}

/** 失败原因：显示后端按当前语言渲染的 `failureMessage`，取不到时回落到原始码。 */
export function operationFailureReason(record: OperationRecordOutputDto): string | null {
  if (!record.failureCode) {
    return null;
  }
  //#if (IncludeLocalization)
  return record.failureMessage ?? record.failureCode;
  //#else
  const text = FAILURE_REASONS[record.failureCode];
  return (
    record.failureMessage ??
    (text ? fillPlaceholders(text, parseFailureData(record.failureData)) : null) ??
    record.failureCode
  );
  //#endif
}
//#if (!IncludeLocalization)

/** 与后端 `LocalizationPlaceholders.Fill` 同一规则：按名替换 `{name}`，没有对应参数的原样保留。 */
function fillPlaceholders(text: string, data: Record<string, unknown>): string {
  return text.replace(/\{(\w+)\}/g, (placeholder, name: string) =>
    Object.hasOwn(data, name) ? String(data[name] ?? '') : placeholder,
  );
}

/** `failureData` 是后端序列化的 JSON 参数对象；解析失败按无参数处理，一条坏记录不该整行渲染不出来。 */
function parseFailureData(raw: string | undefined): Record<string, unknown> {
  if (!raw) {
    return {};
  }
  try {
    const parsed: unknown = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}
//#endif
