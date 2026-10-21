namespace Leistd.Timing;

/// <summary>使用 <see cref="TimeProvider"/> 提供 UTC 时间。</summary>
/// <remarks>归一化时未指定类型的值按 UTC 处理，本地时间转换为 UTC。</remarks>
/// <param name="timeProvider">时间源；省略时使用 <see cref="TimeProvider.System"/>。</param>
public class UtcClockProvider(TimeProvider timeProvider) : IClock
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>使用系统时间源创建提供器，供未注册 <see cref="TimeProvider"/> 的宿主经依赖注入解析。</summary>
    public UtcClockProvider() : this(TimeProvider.System)
    {
    }

    /// <inheritdoc />
    public DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public DateTime Normalize(DateTime dateTime)
    {
        if (dateTime.Kind == DateTimeKind.Unspecified)
        {
            return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
        }

        if (dateTime.Kind == DateTimeKind.Local)
        {
            return dateTime.ToUniversalTime();
        }

        return dateTime;
    }
}
