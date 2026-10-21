/** 读出响应体；非 2xx 时连状态、地址与响应体一起抛出，失败原因不必再去翻 API 日志。 */
export async function json<T = unknown>(response: {
  ok(): boolean;
  status(): number;
  url(): string;
  text(): Promise<string>;
}): Promise<T> {
  const body = await response.text();
  if (!response.ok()) throw new Error(`${response.status()} ${response.url()}: ${body}`);
  return (body ? JSON.parse(body) : undefined) as T;
}
