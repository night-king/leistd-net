namespace Leistd.Notifications.EntityFrameworkCore.Options;

/// <summary>通知保留期。配置节 <c>Leistd:Notifications:Retention</c>。</summary>
/// <remarks>默认开启；已读与未读分开计时，未读保留更久。</remarks>
public sealed class NotificationRetentionOptions
{
    /// <summary>配置节路径。</summary>
    public const string SectionName = "Leistd:Notifications:Retention";

    /// <summary>是否启用到期清理，默认开启。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>已读通知的保留天数（1–3650），按创建时间计，默认 90。</summary>
    public int ReadRetentionDays { get; set; } = 90;

    /// <summary>未读通知的保留天数（1–3650），按创建时间计，默认 365；不得短于已读的保留天数。</summary>
    public int UnreadRetentionDays { get; set; } = 365;

    /// <summary>每天执行的 UTC 小时（0–23），默认 19。</summary>
    public int DailyRunHourUtc { get; set; } = 19;

    /// <summary>单批删除的条数（100–10000），默认 500；每批一个事务。</summary>
    public int BatchSize { get; set; } = 500;
}
