import { HttpErrorResponse } from '@angular/common/http';

/**
 * 字段级错误。数组形的 `field` 按服务端 JSON 命名策略写出（与请求体字段同名）；
 * 官方字典形的键是属性名（PascalCase）。只用于分组展示消息，不按它定位表单控件。
 */
export interface ApiErrorItem {
  code?: string;
  detail?: string;
  field?: string;
}

/**
 * RFC 9457 问题详情。`errors` 有两种形状：
 * 本框架的数组 `[{ field, detail, code }]`（逐字段带错误码），
 * 与官方 `HttpValidationProblemDetails` 的字典 `{ 字段: [消息…] }`（Minimal API 的 `AddValidation()`，键为属性名）。
 * 读取时逐个成员校验类型与格式，结果里只剩可以公开展示的值。
 */
interface ApiProblemDetails {
  code?: string;
  detail?: string;
  errors: ApiErrorItem[];
  title?: string;
  traceId?: string;
}

/**
 * 失败的分类，由归一化层按状态码、响应体与请求方法得出：
 * - `problem`：有效的 Problem Details，文案取自字段错误、`detail`、`title`；
 * - `invalidResponse`：响应无法识别（2xx 解析失败、网关 HTML、空正文等）；
 * - `server`：5xx 且没有可用的 Problem Details；
 * - `network`：没有收到响应（status 0）；
 * - `uncertain`：写请求（非安全方法）没有收到响应、遇到 5xx 或 2xx 解析失败，操作可能已经生效。
 */
export type ApplicationHttpErrorCategory =
  'problem' | 'invalidResponse' | 'server' | 'network' | 'uncertain';

/**
 * 请求方法与各分类的公开兜底文案（本地化后的文字）；缺省时使用内置英文。
 * 不携带请求正文、凭据或服务端异常详情。
 */
export interface ApplicationHttpErrorOptions {
  /** 请求方法；非安全方法在结果不确定时归为 `uncertain`，缺省按安全方法处理。 */
  method?: string;
  invalidResponseMessage?: string;
  networkErrorMessage?: string;
  /** 有效 Problem Details 只带错误码、没有可展示文字时使用。 */
  requestFailedMessage?: string;
  serverErrorMessage?: string;
  traceIdLabel?: string;
  uncertainResultMessage?: string;
}

const DEFAULT_NETWORK_ERROR_MESSAGE =
  'Unable to reach the server. Check your connection and try again.';
const DEFAULT_SERVER_ERROR_MESSAGE =
  'An unexpected error occurred. Please refresh the page and try again.';
const DEFAULT_INVALID_RESPONSE_MESSAGE =
  'The server response could not be processed. Refresh the page and try again.';
const DEFAULT_UNCERTAIN_RESULT_MESSAGE =
  'The operation may have completed. Check the result before trying again.';
const DEFAULT_REQUEST_FAILED_MESSAGE = 'The request could not be completed.';

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS', 'TRACE']);
const CODE_PATTERN = /^[A-Za-z0-9][A-Za-z0-9:._-]{0,127}$/;
const TRACE_ID_PATTERN = /^[A-Za-z0-9|][A-Za-z0-9:|.-]{0,127}$/;

export class ApplicationHttpError extends Error {
  readonly category: ApplicationHttpErrorCategory;
  readonly code?: string;
  readonly details: readonly ApiErrorItem[];
  /** 内部诊断用：原始状态码。展示文案不由它推导。 */
  readonly status: number;
  readonly traceId?: string;
  readonly traceIdLabel: string;
  /** 内部诊断用：请求地址，不进入展示文案。 */
  readonly url: string | null;

  private constructor(
    response: HttpErrorResponse,
    category: ApplicationHttpErrorCategory,
    message: string,
    problem: ApiProblemDetails,
    traceIdLabel: string,
  ) {
    // 原始响应只经 cause 保留供诊断，不参与展示文案。
    super(message, { cause: response });
    this.name = 'ApplicationHttpError';
    this.category = category;
    this.status = response.status;
    this.traceId = problem.traceId;
    this.traceIdLabel = traceIdLabel;
    this.url = response.url;
    this.code = problem.code || problem.errors.find((item) => item.code)?.code;
    this.details = problem.errors;
  }

  /**
   * 公开文案只取 Problem Details 里校验过的成员（字段错误、`detail`、`title`），
   * 不从 statusText（Angular 会把空值补成 "OK"）、字符串或 HTML 正文、浏览器异常、URL 回落；
   * 其余分类使用 `options` 给出的通用文案。写请求结果不确定时不自动重放，由用户核实后再操作。
   */
  static from(
    response: HttpErrorResponse,
    options: ApplicationHttpErrorOptions = {},
  ): ApplicationHttpError {
    const traceIdLabel = options.traceIdLabel ?? 'Trace ID';
    const create = (
      category: ApplicationHttpErrorCategory,
      message: string,
      problem: ApiProblemDetails = { errors: [] },
    ) => new ApplicationHttpError(response, category, message, problem, traceIdLabel);

    const unsafe = !!options.method && !SAFE_METHODS.has(options.method.toUpperCase());
    const uncertainMessage = options.uncertainResultMessage ?? DEFAULT_UNCERTAIN_RESULT_MESSAGE;

    if (response.status === 0) {
      return unsafe
        ? create('uncertain', uncertainMessage)
        : create('network', options.networkErrorMessage ?? DEFAULT_NETWORK_ERROR_MESSAGE);
    }

    const problem = readProblemDetails(response.error);
    // 只保留诊断用的标识，字段错误与文字不再展示
    const diagnostics: ApiProblemDetails = {
      code: problem.code,
      errors: [],
      traceId: problem.traceId,
    };

    // 2xx 进到这里只可能是响应体解析失败：请求已被服务端处理
    if (response.status >= 200 && response.status < 300) {
      return unsafe
        ? create('uncertain', uncertainMessage, diagnostics)
        : create(
            'invalidResponse',
            options.invalidResponseMessage ?? DEFAULT_INVALID_RESPONSE_MESSAGE,
            diagnostics,
          );
    }

    if (response.status >= 500 && unsafe) {
      return create('uncertain', uncertainMessage, diagnostics);
    }

    if (!isProblem(problem)) {
      return response.status >= 500
        ? create('server', options.serverErrorMessage ?? DEFAULT_SERVER_ERROR_MESSAGE, diagnostics)
        : create(
            'invalidResponse',
            options.invalidResponseMessage ?? DEFAULT_INVALID_RESPONSE_MESSAGE,
            diagnostics,
          );
    }

    // 有字段错误时由它们组成消息：校验失败时 detail / title 只是概括，
    // 字段错误才是服务端已按当前语言本地化的具体原因。
    const fieldMessages = firstMessagePerField(problem.errors);
    const message =
      (fieldMessages.length > 0 ? fieldMessages.join('\n') : undefined) ||
      problem.detail ||
      problem.title ||
      options.requestFailedMessage ||
      DEFAULT_REQUEST_FAILED_MESSAGE;
    return create('problem', message, problem);
  }
}

/**
 * 展示用文案。通用文案（非 `problem`）与 5xx 在有追踪号时附上，便于反馈给运维定位。
 */
export function applicationErrorMessage(error: unknown): string {
  if (
    error instanceof ApplicationHttpError &&
    error.traceId &&
    (error.category !== 'problem' || error.status >= 500)
  ) {
    return `${error.message} (${error.traceIdLabel}: ${error.traceId})`;
  }
  return error instanceof Error ? error.message : 'An unexpected error occurred.';
}

/**
 * 每个字段只取第一条：同一字段常同时违反多条规则（空串既"必填"又"长度不足"），
 * 全列出来是重复噪音，改掉第一条后下一条自然会再报。
 */
function firstMessagePerField(details: readonly ApiErrorItem[]): string[] {
  const seenFields = new Set<string>();
  const messages: string[] = [];
  for (const item of details) {
    const text = item.detail;
    const field = item.field ?? '';
    if (!text || seenFields.has(field) || messages.includes(text)) {
      continue;
    }
    seenFields.add(field);
    messages.push(text);
  }
  return messages;
}

function readProblemDetails(value: unknown): ApiProblemDetails {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return { errors: [] };
  }
  const problem = value as Record<string, unknown>;
  return {
    code: readCode(problem['code']),
    detail: readText(problem['detail']),
    errors: readErrors(problem['errors']),
    title: readText(problem['title']),
    traceId: readTraceId(problem['traceId']),
  };
}

function isProblem(problem: ApiProblemDetails): boolean {
  return !!(problem.code || problem.detail || problem.title || problem.errors.length > 0);
}

/** 从 Problem Details 读取稳定业务错误码。 */
export function apiErrorCode(value: unknown): string | undefined {
  return readProblemDetails(value).code;
}

function readCode(value: unknown): string | undefined {
  return typeof value === 'string' && CODE_PATTERN.test(value) ? value : undefined;
}

function readTraceId(value: unknown): string | undefined {
  return typeof value === 'string' && TRACE_ID_PATTERN.test(value) ? value : undefined;
}

function readText(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value : undefined;
}

/** 两种形状都展开为 `ApiErrorItem`：字典的每条消息各成一项，保留字段名与全部消息。 */
function readErrors(errors: unknown): ApiErrorItem[] {
  if (Array.isArray(errors)) {
    return errors.flatMap((item: unknown): ApiErrorItem[] => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) {
        return [];
      }
      const value = item as Record<string, unknown>;
      const field = typeof value['field'] === 'string' ? value['field'] : undefined;
      const detail = readText(value['detail']);
      const code = readCode(value['code']);
      if (!detail && !code) {
        return [];
      }
      return [
        {
          ...(field !== undefined ? { field } : {}),
          ...(detail ? { detail } : {}),
          ...(code ? { code } : {}),
        },
      ];
    });
  }

  if (!errors || typeof errors !== 'object') {
    return [];
  }

  return Object.entries(errors).flatMap(([field, messages]) =>
    (Array.isArray(messages) ? messages : [messages])
      .map(readText)
      .filter((detail): detail is string => !!detail)
      .map((detail) => ({ field, detail })),
  );
}
