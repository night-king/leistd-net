# 核心原语：时钟与脱敏

`Leistd.Core` 提供可注入的 UTC 时钟和文本脱敏，供各组件复用。

## 何时使用

| 场景 | 用法 |
| --- | --- |
| 可控制当前时间、测试时间边界 | 注入 `IClock` |
| 按业务时区统计自然日 | `GetMidnightInUtc(timeZone)` |
| 归一化外部时间 | `IClock.Normalize(dateTime)` |
| 脱敏邮箱 | `TextRedactor.RedactEmail(address)` |
| 脱敏手机号、卡号或证件号 | `TextRedactor.RedactPartially(value, keepStart, keepEnd)` |

## 安装

上层组件已传递引用时无需单独安装：

```bash
dotnet add package Leistd.Core
```

## 注册

本包不注册服务。DDD 基础设施已注册默认时钟；单独使用时手动注册：

```csharp
using Leistd.Timing;
using Microsoft.Extensions.DependencyInjection;

services.AddSingleton<IClock, UtcClockProvider>();
```

## 使用

```csharp
using Leistd.Timing;

public class DailyReportService(IClock clock)
{
    public (DateTime from, DateTime to) TodayRangeUtc(TimeZoneInfo businessTimeZone)
        => (clock.GetMidnightInUtc(businessTimeZone), clock.Now);
}
```

时区显式来自业务配置；例如当前 UTC 为 `2026-05-27T20:00:00Z`，`Asia/Shanghai` 的当天零点对应 `2026-05-27T16:00:00Z`。

## 接口参考

时钟位于 `Leistd.Timing`，脱敏位于 `Leistd.Redaction`。

| 成员 | 契约 |
| --- | --- |
| `IClock.Now` | 当前 UTC 时间 |
| `IClock.Normalize(dateTime)` | `Unspecified` 视为 UTC，`Local` 转 UTC，`Utc` 原样返回 |
| `UtcClockProvider` | 无状态；使用注入的 `TimeProvider`，默认 `TimeProvider.System`；测试可用 `FakeTimeProvider` |
| `GetMidnightInUtc(timeZone)` | 指定时区今日零点对应的 UTC 时刻 |
| `GetUtcOffsetHours(timeZone)` | 指定时区当前相对 UTC 的小时偏移，含夏令时 |
| `TextRedactor.RedactEmail(address)` | 本地部从首个字母或数字起最多保留 3 位，且不超过本地部的一半；域名完整保留，无法取得域名时不回显原文 |
| `TextRedactor.RedactPartially(value, keepStart, keepEnd)` | 保留两端指定字符数；不足以保留两端时整体掩码 |

## 注意事项

- 持久化与跨服务传递用 UTC，按用户时区展示由呈现层转换；不能用宿主 `TimeZoneInfo.Local` 代替业务时区。
- `IClock.Now` 是 `DateTime`，精度 100 纳秒；存进 PostgreSQL（Npgsql）的 `timestamp`/`timestamptz` 只保留到微秒，多出的部分写入时丢弃。
  所以内存里的时刻与读回的值可能差不到 1 微秒：比较持久化前后的时刻时，预期值先按微秒对齐，或换新作用域读回后再比较；
  不为此改时钟或加全局转换器。
- 掩码固定为三个星号，不反映原值长度；邮箱示例：`alice@` → `al***@`、`bob@` → `b***@`、`a@` → `***@`。
- 调用方决定是否脱敏及保留位数，写日志与对外展示共用同一形态，不按部署环境切换。集中日志脱敏策略可用 `Microsoft.Extensions.Compliance.Redaction`，展示仍由本类处理。
- Core 不定义通用异常基类；优先使用 .NET 内置异常，可预期业务失败使用 `BusinessException`。

## 相关

- [异常处理](./exception-handling.md)
