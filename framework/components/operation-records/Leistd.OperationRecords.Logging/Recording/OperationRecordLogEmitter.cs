using System.Text.Json;
using Leistd.OperationRecords.Logging.Constants;
using Leistd.OperationRecords.Models;
using Microsoft.Extensions.Logging;

namespace Leistd.OperationRecords.Logging.Recording;

// 把一条已冻结的记录写成一条结构化日志，字段与数据库存储逐一对应，失败参数除外：
// 它是词条占位的原值，可能含提交者的邮箱等联系方式，日志只记参数名；原值只进数据库存储
internal sealed class OperationRecordLogEmitter(ILoggerFactory loggerFactory)
{
    private const string MessageTemplate =
        "Operation {OperationAction} {OperationOutcome} on {OperationTargetId} ({OperationTargetName}) "
        + "by {OperationActorId} ({OperationActorName}) of tenant {OperationActorTenantId} in tenant {OperationTenantId}; "
        + "basis {OperationAuthorizationBasis}, visibility {OperationVisibility}, impersonator {OperationImpersonatorId} ({OperationImpersonatorName}), "
        + "failure {OperationFailureCode} {OperationFailureDataKeys} {OperationFailureDetail}, "
        + "at {OperationTime:O}, correlation {OperationCorrelationId}, record {OperationRecordId}";

    private readonly ILogger _logger = loggerFactory.CreateLogger(OperationRecordLogging.CategoryName);
    private readonly ILogger _deliveryLogger = loggerFactory.CreateLogger(OperationRecordLogging.DeliveryCategoryName);

    public bool IsEnabled => _logger.IsEnabled(LogLevel.Information);

    public void Emit(OperationRecordInfo record)
    {
        var succeeded = record.Outcome == OperationRecordOutcome.Succeeded;
        _logger.Log(
            succeeded ? LogLevel.Information : LogLevel.Warning,
            new EventId(succeeded ? OperationRecordLogging.SucceededEventId : OperationRecordLogging.FailedEventId, "OperationRecord"),
            MessageTemplate,
            record.Action,
            record.Outcome,
            record.TargetId,
            record.TargetName,
            record.ActorId,
            record.ActorName,
            record.ActorTenantId,
            record.TenantId,
            record.AuthorizationBasis,
            record.Visibility,
            record.ImpersonatorId,
            record.ImpersonatorName,
            record.FailureCode,
            DataKeys(record.FailureData),
            record.FailureDetail,
            record.CreationTime,
            record.CorrelationId,
            record.Id);
    }

    // 失败参数是 OperationFailure 写出的扁平 JSON 对象；解析不了时不记，也不回落到原文
    private static string[] DataKeys(string? failureData)
    {
        if (string.IsNullOrEmpty(failureData))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(failureData);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? [.. document.RootElement.EnumerateObject().Select(property => property.Name)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // 业务已提交后的写出：失败不改变业务结果，只在另一类别尽力报告记录标识与动作码
    public void EmitCommitted(OperationRecordInfo record)
    {
        try
        {
            Emit(record);
        }
        catch (Exception exception)
        {
            try
            {
                _deliveryLogger.LogCritical(
                    new EventId(OperationRecordLogging.DeliveryFailedEventId, "OperationRecordDeliveryFailed"),
                    exception,
                    "The operation {OperationAction} was committed but its record {OperationRecordId} could not be written to the log",
                    record.Action,
                    record.Id);
            }
            catch
            {
                // 两个类别都写不出去：日志管道整体不可用，再抛只会把已提交的业务报成失败。
            }
        }
    }
}
