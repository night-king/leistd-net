/**
 * 仪表盘的登录活动样本。Mock 没有逐次登录的流水，按「工作日白天高、夜里与周末低」的固定规律
 * 给每个 15 分钟时段生成成功与失败次数：结果只取决于时段本身，不随运行时间抖动，截图与用例都可复现；
 * 时段切到 15 分钟，按 +05:30、+05:45 这类时区切出的桶也能分得开。白天的成功与失败另按 28 天、21 天
 * 一轮逐日加量：只按周重复的话，与上一期对比恒为 0%。
 */
export interface MockLoginSlot {
  succeeded: number;
  failed: number;
}

export const LOGIN_SLOT_MS = 15 * 60_000;

/** 以 `slotStart`（UTC 毫秒，15 分钟对齐）开始的那个时段的登录次数。 */
export function loginSlot(slotStart: number): MockLoginSlot {
  const index = Math.floor(slotStart / LOGIN_SLOT_MS);
  const date = new Date(slotStart);
  const weekend = date.getUTCDay() === 0 || date.getUTCDay() === 6;
  const busy = date.getUTCHours() >= 1 && date.getUTCHours() < 11;
  const day = Math.floor(slotStart / 86_400_000);
  const ramp = day % 28;
  const succeeded = weekend
    ? Number(index % 5 === 0)
    : Number((index * 7) % 3 !== 0) +
      Number(busy && index % 2 === 0) +
      Number(busy && index % 28 < ramp);
  return {
    succeeded,
    failed: Number((index * 13) % 29 === 0) + Number(busy && index % 60 < day % 21),
  };
}

/** [from, to) 内各时段之和。 */
export function sumLoginSlots(
  from: number,
  to: number,
  pick: (slot: MockLoginSlot) => number,
): number {
  let total = 0;
  for (
    let slot = Math.ceil(from / LOGIN_SLOT_MS) * LOGIN_SLOT_MS;
    slot < to;
    slot += LOGIN_SLOT_MS
  ) {
    total += pick(loginSlot(slot));
  }
  return total;
}
