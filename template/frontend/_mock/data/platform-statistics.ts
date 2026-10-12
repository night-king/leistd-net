/**
 * 仪表盘的登录活动样本。Mock 没有逐次登录的流水，按「工作日高、周末低」的固定规律生成每天的
 * 成功与失败次数，结果不随运行时间抖动，截图与用例都可复现。
 */
export interface MockLoginDay {
  succeeded: number;
  failed: number;
}

/** 距今 `daysAgo` 天（UTC 日，0 为今天）的登录活动。 */
export function loginActivity(daysAgo: number): MockLoginDay {
  const date = new Date(Date.now() - daysAgo * 86_400_000);
  const weekend = date.getUTCDay() === 0 || date.getUTCDay() === 6;
  const wave = (daysAgo * 7) % 11;
  const succeeded = (weekend ? 14 : 38) + wave * 2;
  return { succeeded, failed: (daysAgo * 5) % 4 };
}
