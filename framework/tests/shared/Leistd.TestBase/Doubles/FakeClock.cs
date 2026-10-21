using Leistd.Timing;

namespace Leistd.TestBase.Doubles;

/// <summary>
/// 可控时钟：测试中设定固定的 <see cref="Now"/>，替代 <see cref="UtcClockProvider"/>。
/// 时间固定为 UTC，与 <c>UtcClockProvider</c> 同口径。
/// </summary>
public sealed class FakeClock : IClock
{
    /// <summary>固定的当前时间（可读写，便于测试推进时间）。</summary>
    public DateTime Now { get; set; }

    /// <param name="now">初始当前时间；缺省为 2026-01-01 00:00:00 UTC。</param>
    public FakeClock(DateTime? now = null)
    {
        Now = now ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>把入参归一化为 UTC。</summary>
    public DateTime Normalize(DateTime dateTime) => dateTime.Kind switch
    {
        DateTimeKind.Utc => dateTime,
        DateTimeKind.Local => dateTime.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
    };
}
