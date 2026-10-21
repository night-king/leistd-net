namespace Leistd.Timing;

/// <summary>提供当前 UTC 时间；按用户时区展示时由呈现层转换。</summary>
public interface IClock
{
    /// <summary>当前 UTC 时间。</summary>
    DateTime Now { get; }

    /// <summary>将时间归一化为 UTC。</summary>
    DateTime Normalize(DateTime dateTime);
}
