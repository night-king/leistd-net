using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.Options;
using Leistd.Security.Users;
using Leistd.Timing;
using Leistd.Tracing.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.OperationRecords.Recording;

// IOperationRecorder 的默认实现：从当前上下文补齐操作人、时间与链路标识。
// 时间由本类填充，不依赖宿主给 DbContext 挂审计拦截器。
// 本类只决定记录写进哪一层（TenantId），事务边界由存储按结果执行（见 IOperationRecordWriter）。
internal sealed class OperationRecorder(
    IOperationRecordWriter writer,
    IOperationActionDefinitionManager actionDefinitions,
    ICurrentTenant currentTenant,
    ICurrentUser currentUser,
    ICorrelationIdProvider correlationIdProvider,
    IClock clock,
    IOptions<OperationRecordOptions> options,
    ILogger<OperationRecorder> logger,
    RecordedFailureTracker recordedFailures) : IOperationRecorder
{
    public Task RecordSucceededAsync(
        string action,
        OperationTarget target,
        string authorizationBasis,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(action, authorizationBasis);

        // 宿主可见的成功记录只能写在宿主上下文里：租户上下文的事务连的是租户的库
        if (definition.Visibility == OperationVisibility.Host && currentTenant.Id is { } tenantId)
        {
            throw new InvalidOperationException(
                $"Operation action '{action}' is host-visible but succeeded inside tenant '{tenantId}'. "
                + "Register it as tenant-visible, or record it after switching to the host context.");
        }

        // 调用阶段的异常照常上抛。数据库写入随业务事务失败；日志适配排队到提交后输出，
        // 提交后的投递故障由适配报告，不能再把已提交的业务改报成失败。
        return writer.InsertAsync(
            Create(action, target, authorizationBasis, definition, currentTenant.Id,
                OperationRecordOutcome.Succeeded, OperationFailure.None),
            cancellationToken);
    }

    public async Task RecordFailedAsync(
        string action,
        OperationTarget target,
        string authorizationBasis,
        OperationFailure failure = default)
    {
        // 校验放在 try 之外：catch 只吞写入故障，参数与动作码错误是编码错误，必须抛出
        var definition = GetDefinition(action, authorizationBasis);

        // 宿主可见的失败记录写进宿主层；失败记录独立提交，不牵动业务事务
        var tenantId = definition.Visibility == OperationVisibility.Host ? null : currentTenant.Id;

        try
        {
            // 不可取消：请求中断不影响这条记录
            await writer.InsertAsync(
                Create(action, target, authorizationBasis, definition, tenantId,
                    OperationRecordOutcome.Failed, failure),
                CancellationToken.None);

            // 写出之后才登记：写库失败时端点兜底仍应补记
            recordedFailures.MarkRecorded(action, target.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to record a rejected operation {Action} on {TargetId}; the rejection itself stands",
                action,
                target.Id);
        }
    }

    // 动作码与授权依据不得为空白（空白目标已由 OperationTarget 收敛为 None）；
    // 动作码必须已登记，记录的可见性取自定义。
    private IOperationActionDefinition GetDefinition(string action, string authorizationBasis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationBasis);

        return actionDefinitions.GetOrNull(action)
            ?? throw new InvalidOperationException(
                $"Operation action '{action}' is not registered. "
                + "Register it through an IOperationActionDefinitionProvider.");
    }

    // 从当前上下文补齐记录。超长字段会在存储侧被静默截断，这里先记 Warning
    private OperationRecordInfo Create(
        string action,
        OperationTarget target,
        string authorizationBasis,
        IOperationActionDefinition definition,
        Guid? tenantId,
        OperationRecordOutcome outcome,
        OperationFailure failure)
    {
        var correlationId = correlationIdProvider.Get();
        var claimTypes = options.Value;

        // 读 claim 原始值而不是 ICurrentUser.Id：后者只在 sub 是 GUID 时有值，机器与后台作业主体不是。
        // 自证类动作在匿名请求里完成：操作人取目标、所属租户取当前上下文，Actor 层的本人才能看到它。
        var authenticated = currentUser.IsAuthenticated;
        var actorId = authenticated
            ? currentUser.SubjectId
            : definition.TargetIsActor ? target.Id : null;
        // 操作人所属租户取自其主体；没有主体时退回请求所在的租户上下文（匿名请求只能来自那里）
        var actorTenantId = authenticated ? currentUser.TenantId : currentTenant.Id;
        // 操作人名是快照：自证类动作取目标名，改名或销号之后审计仍回答"当时是谁"
        var actorName = authenticated
            ? currentUser.Name ?? currentUser.Username
            : definition.TargetIsActor ? target.Name : null;
        var impersonatorId = currentUser.FindClaim(claimTypes.ImpersonatorIdClaimType)?.Value;
        var impersonatorName = currentUser.FindClaim(claimTypes.ImpersonatorNameClaimType)?.Value;

        WarnIfWouldTruncate(nameof(action), action, OperationRecordInfo.MaxActionLength);
        WarnIfWouldTruncate("targetId", target.Id, OperationRecordInfo.MaxTargetIdLength);
        WarnIfWouldTruncate("targetName", target.Name, OperationRecordInfo.MaxTargetNameLength);
        WarnIfWouldTruncate(nameof(authorizationBasis), authorizationBasis, OperationRecordInfo.MaxAuthorizationBasisLength);
        WarnIfWouldTruncate("failureCode", failure.Code, OperationRecordInfo.MaxFailureCodeLength);
        WarnIfWouldTruncate("failureDetail", failure.Detail, OperationRecordInfo.MaxFailureDetailLength);
        WarnIfWouldTruncate(nameof(actorId), actorId, OperationRecordInfo.MaxActorIdLength);
        WarnIfWouldTruncate(nameof(actorName), actorName, OperationRecordInfo.MaxActorNameLength);
        WarnIfWouldTruncate(nameof(correlationId), correlationId, OperationRecordInfo.MaxCorrelationIdLength);

        return new OperationRecordInfo
        {
            TenantId = tenantId,
            ActorTenantId = actorTenantId,
            Action = action,
            TargetId = target.Id,
            TargetName = target.Name,
            AuthorizationBasis = authorizationBasis,
            Outcome = outcome,
            Visibility = definition.Visibility,
            FailureCode = failure.Code,
            FailureData = failure.Data,
            FailureDetail = failure.Detail,
            CreationTime = clock.Normalize(clock.Now),
            ActorId = actorId,
            ActorName = actorName,
            ImpersonatorId = impersonatorId,
            ImpersonatorName = impersonatorName,
            CorrelationId = correlationId
        };
    }

    private void WarnIfWouldTruncate(string fieldName, string? value, int maxLength)
    {
        if (value is not null && value.Trim().Length > maxLength)
        {
            logger.LogWarning(
                "Operation record field {Field} is {ActualLength} characters, exceeding the {MaxLength} limit; "
                + "it will be truncated and the audit trail for this event will not be fully accurate.",
                fieldName,
                value.Trim().Length,
                maxLength);
        }
    }
}
