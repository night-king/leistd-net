import { PagedRequestDto } from '../../../shared/dtos/paged-request.dto';

/** 操作结果，与后端 `OperationRecordOutcome` 同名的 camelCase 写法。 */
export type OperationRecordOutcome = 'succeeded' | 'failed';

/** 动作严重度，与后端 `OperationSeverity` 同名的 camelCase 写法。 */
export type OperationSeverity = 'info' | 'notice' | 'critical';

/** 一条操作记录：什么人、在什么时间、做了什么、结果如何。 */
export interface OperationRecordOutputDto {
  id: string;
  /**
   * 业务动作码（如 `user.created`），界面按 `operationRecords.actions.<码>` 查词条渲染；
   * 本项目登记的码由 `check-operation-action-i18n.py` 校验，未登记的码原样显示。
   */
  action: string;
  /** 操作目标标识；没有目标时为 `-`。 */
  targetId: string;

  /** 操作目标名称快照；取不到时为空。授权阶段被拒的记录刻意不回填，界面降级显示标识。 */
  targetName?: string;
  /** 授权依据：权限名，或业务自己的标记。 */
  authorizationBasis: string;
  outcome: OperationRecordOutcome;
  creationTime: string;
  /** 操作人标识；机器主体为 `client:<client_id>`，后台作业由宿主自定前缀。 */
  actorId?: string;
  /** 操作人显示名的快照——当时的名字，不随后来改名而变。 */
  actorName?: string;
  /**
   * 主体是否就是 `targetName` 本人，操作人缺失时据此回落到目标名（如认证前的登录）。由服务端判定：
   * `auth.login.failed` 的目标是未经验证的用户名，前端自行回落会让任何人往操作人列里写文本。
   */
  actorIsTarget?: boolean;
  /** 模拟登录时真实操作人的显示名；有值时 `actorName` 是被模拟的租户用户，两者要一起显示。 */
  impersonatorName?: string;
  /** 失败原因的错误码，成功时为空；存码而非句子，读者按自己的语言渲染。 */
  failureCode?: string;

  /** 失败原因的本地化占位参数（JSON 对象字符串）。 */
  failureData?: string;

  /** 后端按本次请求语言渲染的失败原因；未本地化或缺词条时为空，界面回落显示 `failureCode`。 */
  failureMessage?: string;

  /** 面向排查的技术说明，仅宿主可见；裁剪在服务端完成。 */
  failureDetail?: string;

  /** 链路标识，用于在日志中查这次调用；仅宿主可见。 */
  correlationId?: string;

  /** 操作发生时的租户，仅宿主可见，宿主上下文的操作为空；用于识别调用宿主接口被拒的租户用户。 */
  actorTenantId?: string;
}

/**
 * 操作记录查询入参。后端不支持排序（固定按时间倒序），因此去掉基类的 `sorting`，
 * 而不是留一个传了也不起作用的字段。
 */
export interface GetOperationRecordsInputDto extends Omit<PagedRequestDto, 'sorting'> {
  /** 关键字，同时匹配动作码、目标标识与操作人名。 */
  keyword?: string;
  /** 起始时刻（含），ISO 8601 UTC；调用方须把展示时区的日期换算成 UTC。 */
  startTime?: string;
  /** 结束时刻（含），ISO 8601 UTC；选中某天时取当天 23:59:59.999 再换算。 */
  endTime?: string;

  /** 按类别筛选（后端展开成动作码）。查询串为重复键，服务层须逐个 `append`，`set` 只保留最后一个。 */
  categories?: string[];

  /** 按动作码筛选；与 {@link categories} 同时给出时取交集（维度之间是 AND）。 */
  actions?: string[];

  /** 按结果筛选：`succeeded` 或 `failed`（取自 URL，类型不收窄）；其他写法会被后端以字段 `outcome` 的 400 拒绝。 */
  outcome?: string;
}

/**
 * 导出入参：与查询同一组筛选字段，但没有分页，取筛选结果的前 N 条。`limit` 由后端封顶（10000），
 * 超出返回 400。
 */
export interface ExportOperationRecordsInputDto {
  keyword?: string;
  startTime?: string;
  endTime?: string;
  categories?: string[];
  actions?: string[];
  outcome?: string;
  /** 导出条数；不传则由后端取上限。 */
  limit?: number;
}

/** 筛选项：类别与动作由服务端下发，界面不硬编码动作码。 */
export interface OperationRecordFilterOptionsDto {
  /** 出现过的类别。 */
  categories: string[];
  /** 可选的动作，含所属类别与严重度。 */
  actions: OperationActionOptionDto[];
}

/** 一个可选的操作动作。 */
export interface OperationActionOptionDto {
  /** 动作码，界面按它查句子模板。 */
  code: string;
  /** 所属类别，用于按类别联动。 */
  category: string;
  severity: OperationSeverity;
}
