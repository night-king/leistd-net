namespace Leistd.OperationRecords.Logging.Constants;

/// <summary>结构化日志输出的公开约定：日志类别与事件标识。</summary>
/// <remarks>
/// 部署按这里的类别配置日志级别与采集；记录以 <see cref="Microsoft.Extensions.Logging.LogLevel.Information"/>（成功）
/// 与 <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/>（失败）写出，因此该类别至少要开到 Information。
/// </remarks>
public static class OperationRecordLogging
{
    /// <summary>操作记录的日志类别。</summary>
    public const string CategoryName = "Leistd.OperationRecords";

    /// <summary>输出故障的日志类别：记录写出失败时，在这里报告记录标识。</summary>
    public const string DeliveryCategoryName = "Leistd.OperationRecords.Delivery";

    /// <summary>成功记录的事件标识。</summary>
    public const int SucceededEventId = 7100;

    /// <summary>失败记录的事件标识。</summary>
    public const int FailedEventId = 7101;

    /// <summary>业务已提交、但成功记录未能写出时的事件标识。</summary>
    public const int DeliveryFailedEventId = 7102;
}
