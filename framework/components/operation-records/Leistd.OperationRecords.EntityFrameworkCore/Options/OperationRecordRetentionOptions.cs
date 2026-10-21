namespace Leistd.OperationRecords.EntityFrameworkCore.Options;

/// <summary>操作记录保留期。配置节 <c>Leistd:OperationRecords:Retention</c>。</summary>
/// <remarks>
/// <para>默认关闭；启用时保留天数必填，组件不提供默认天数。</para>
/// <para>到期记录搬去归档表，不是删除。</para>
/// <para>每轮在本轮作用域里取一份 <c>IOptionsSnapshot</c>：<see cref="Enabled"/> 作为任务开关在执行前判断，
/// 归档读同一份快照，改完下一轮生效，本轮中途改动不影响已开始的这一轮；执行时刻只在任务排期时取一次。</para>
/// </remarks>
public sealed class OperationRecordRetentionOptions
{
    /// <summary>配置节路径。</summary>
    public const string SectionName = "Leistd:OperationRecords:Retention";

    /// <summary>保留天数下限。</summary>
    public const int MinimumRetentionDays = 30;

    /// <summary>保留天数上限。</summary>
    public const int MaximumRetentionDays = 3650;

    /// <summary>是否启用到期归档，默认关闭。</summary>
    public bool Enabled { get; set; }

    /// <summary>保留天数：早于"当前时刻减去该天数"的记录搬入归档表，取值见上下限常量。</summary>
    /// <remarks><see cref="Enabled"/> 为 <see langword="true"/> 时必填，未填时启动校验失败；关闭时可以不填，填了也照样校验区间。</remarks>
    public int? RetentionDays { get; set; }

    /// <summary>每天执行的 UTC 小时（0–23），默认 18；按 UTC 而非服务器时区。</summary>
    public int DailyRunHourUtc { get; set; } = 18;

    /// <summary>单批搬运的条数（100–10000），默认 500；每批一个事务。</summary>
    public int BatchSize { get; set; } = 500;
}
