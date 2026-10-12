using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Leistd.Data.Paging;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.Dtos;
using Leistd.Security.Users;
using Leistd.Timing;
using Microsoft.Extensions.Localization;

namespace Leistd.OperationRecords.Queries;

// 查询、筛选项与导出共用同一份读者判定与字段裁剪，避免界面看不到的记录能被导出
internal sealed class OperationRecordQueryService(
    IOperationRecordReader store,
    IOperationActionDefinitionManager actionDefinitions,
    IOperationRecorder recorder,
    ICurrentTenant currentTenant,
    ICurrentUser currentUser,
    IClock clock,
    IStringLocalizer? localizer = null) : IOperationRecordQueryService
{
    public async Task<PagedResult<OperationRecordOutputDto>> GetPagedListAsync(
        GetOperationRecordPagedInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);

        var reader = ResolveReader();

        // 筛选展开为空时返回空页：存储把空集合当“不过滤”
        var actions = ResolveRequestedActions(input.Categories, input.Actions, reader.IsHost);
        if (actions is { Count: 0 })
        {
            return PagedResult<OperationRecordOutputDto>.Empty;
        }

        var page = await store.GetPagedListAsync(
            Filter(reader, input.Keyword, input.StartTime, input.EndTime, actions, input.Outcome),
            input,
            cancellationToken);

        return new PagedResult<OperationRecordOutputDto>(page.TotalCount, [.. page.Items.Select(record => Map(record, reader.IsHost))]);
    }

    public Task<OperationRecordFilterOptionsOutputDto> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        // 筛选项按可见性裁剪
        var visible = VisibleDefinitions(ResolveReader().IsHost).ToList();

        return Task.FromResult(new OperationRecordFilterOptionsOutputDto
        {
            Categories = [.. visible.Select(definition => definition.Category).Distinct(StringComparer.Ordinal)],
            Actions =
            [
                .. visible.Select(definition => new OperationActionOptionDto
                {
                    Code = definition.Code,
                    Category = definition.Category,
                    Severity = definition.Severity
                })
            ]
        });
    }

    public async Task<OperationRecordExportFileDto> ExportAsync(
        ExportOperationRecordsInputDto input,
        OperationRecordExportAudit audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(audit);
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);

        var reader = ResolveReader();
        var actions = ResolveRequestedActions(input.Categories, input.Actions, reader.IsHost);

        // 筛选展开为空时只导出表头，与分页空页同语义
        IReadOnlyList<OperationRecordOutputDto> rows = [];
        if (actions is not { Count: 0 })
        {
            var page = await store.GetPagedListAsync(
                Filter(reader, input.Keyword, input.StartTime, input.EndTime, actions, input.Outcome),
                new PageRequest { Limit = input.Limit },
                cancellationToken);
            rows = [.. page.Items.Select(record => Map(record, reader.IsHost))];
        }

        var content = BuildCsv(rows, reader.IsHost);

        // 文件生成之后才记，失败的导出不留记录
        await recorder.RecordSucceededAsync(audit.Action, OperationTarget.None, audit.AuthorizationBasis, cancellationToken);

        return new OperationRecordExportFileDto(
            content,
            "text/csv",
            $"operation-records-{clock.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.csv");
    }

    private (OperationRecordVisibilityScope Scope, bool IsHost) ResolveReader()
    {
        if (currentTenant.Id is null)
        {
            return (OperationRecordVisibilityScope.Host, true);
        }

        // 与记录器取操作人的口径一致（claim 原始值与主体的租户 claim），机器主体才能读到自己的 Actor 层记录
        return (OperationRecordVisibilityScope.ForTenantReader(currentUser.SubjectId, currentUser.TenantId), false);
    }

    private static OperationRecordFilter Filter(
        (OperationRecordVisibilityScope Scope, bool IsHost) reader,
        string? keyword,
        DateTime? startTime,
        DateTime? endTime,
        IReadOnlyCollection<string>? actions,
        OperationRecordOutcome? outcome) => new()
        {
            Scope = reader.Scope,
            Keyword = keyword,
            StartTime = startTime,
            EndTime = endTime,
            Actions = actions,
            Outcome = outcome
        };

    // 维度内并集、维度间交集。null 表示没按动作筛，空集合表示筛了但一个都不匹配。
    // 显式给出的动作码同样按可见性过滤，租户读者无法借此试探宿主侧动作。
    private List<string>? ResolveRequestedActions(
        IReadOnlyList<string>? categories,
        IReadOnlyList<string>? requestedActions,
        bool isHost)
    {
        var hasCategories = categories is { Count: > 0 };
        var hasActions = requestedActions is { Count: > 0 };
        if (!hasCategories && !hasActions)
        {
            return null;
        }

        var visible = VisibleDefinitions(isHost).ToList();
        HashSet<string>? codes = null;

        if (hasCategories)
        {
            var categorySet = new HashSet<string>(categories!, StringComparer.Ordinal);
            codes = visible.Where(d => categorySet.Contains(d.Category)).Select(d => d.Code).ToHashSet(StringComparer.Ordinal);
        }

        if (hasActions)
        {
            var visibleCodes = visible.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);
            var requested = requestedActions!.Where(visibleCodes.Contains).ToHashSet(StringComparer.Ordinal);
            if (codes is null)
            {
                codes = requested;
            }
            else
            {
                codes.IntersectWith(requested);
            }
        }

        return [.. codes!];
    }

    private IEnumerable<IOperationActionDefinition> VisibleDefinitions(bool isHost)
        => actionDefinitions.GetAll().Where(definition => isHost || definition.Visibility != OperationVisibility.Host);

    private OperationRecordOutputDto Map(OperationRecordInfo record, bool isHost) => new()
    {
        Id = record.Id,
        Action = record.Action,
        TargetId = record.TargetId,
        TargetName = record.TargetName,
        AuthorizationBasis = record.AuthorizationBasis,
        Outcome = record.Outcome,
        CreationTime = record.CreationTime,
        ActorId = record.ActorId,
        ActorName = record.ActorName,
        // 自证类动作成功时主体刚刚被证实，目标承载的就是"什么人"
        ActorIsTarget = record.ActorId == record.TargetId
                        && record.Outcome == OperationRecordOutcome.Succeeded
                        && actionDefinitions.GetOrNull(record.Action)?.TargetIsActor == true,
        ImpersonatorName = record.ImpersonatorName,
        FailureCode = record.FailureCode,
        FailureData = record.FailureData,
        FailureMessage = LocalizeFailure(record.FailureCode, record.FailureData),
        FailureDetail = isHost ? record.FailureDetail : null,
        CorrelationId = isHost ? record.CorrelationId : null,
        ActorTenantId = isHost ? record.ActorTenantId : null
    };

    // 与错误响应同一个非泛型本地化器与占位符填充；"{码}:Record" 优先于码本身，
    // 审计键在整条文化回落链上都未命中才查码本身。
    private string? LocalizeFailure(string? code, string? data)
    {
        if (localizer is null || code is null)
        {
            return null;
        }

        var localized = localizer[code + RecordTextSuffix];
        if (localized.ResourceNotFound)
        {
            localized = localizer[code];
        }

        return localized.ResourceNotFound ? null : LocalizationPlaceholders.Fill(localized.Value, ReadFailureData(data));
    }

    private const string RecordTextSuffix = ":Record";

    // 参数解析不了时按无参数处理，不让整页查询失败
    private static Dictionary<string, object?>? ReadFailureData(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)(property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText()
                }),
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 带 UTF-8 BOM，Excel 才按 UTF-8 解析。失败原因另起一列按导出请求语言渲染（文件离开系统后无词条可查），
    // 码与参数两列照旧保留。仅宿主列只在宿主导出时成列。
    private static byte[] BuildCsv(IReadOnlyList<OperationRecordOutputDto> rows, bool includeHostOnlyColumns)
    {
        var builder = new StringBuilder();

        List<string> header =
        [
            "CreationTime(UTC)", "Action", "Outcome", "ActorId", "ActorName",
            "ImpersonatorName", "TargetId", "TargetName", "AuthorizationBasis",
            "FailureCode", "FailureData", "FailureMessage"
        ];
        if (includeHostOnlyColumns)
        {
            header.Add("FailureDetail");
            header.Add("CorrelationId");
            header.Add("ActorTenantId");
        }

        builder.AppendLine(string.Join(',', header.Select(EscapeCsv)));

        foreach (var row in rows)
        {
            List<string?> cells =
            [
                row.CreationTime.ToString("o", CultureInfo.InvariantCulture),
                row.Action, OutcomeName(row.Outcome), row.ActorId, row.ActorName,
                row.ImpersonatorName, row.TargetId, row.TargetName, row.AuthorizationBasis,
                row.FailureCode, row.FailureData, row.FailureMessage
            ];
            if (includeHostOnlyColumns)
            {
                cells.Add(row.FailureDetail);
                cells.Add(row.CorrelationId);
                cells.Add(row.ActorTenantId?.ToString());
            }

            builder.AppendLine(string.Join(',', cells.Select(cell => EscapeCsv(cell ?? string.Empty))));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    // 与 HTTP 契约同一种写法（camelCase 名称）；固定写法而不取宿主的 JSON 配置，Core 不依赖 Web 宿主
    private static string OutcomeName(OperationRecordOutcome outcome) => JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString());

    // CSV 转义与公式注入防护：以 = + - @ \t \r 开头的字段前缀单引号，按文本处理。
    private static string EscapeCsv(string value)
    {
        var neutralized = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;

        return neutralized.Contains(',') || neutralized.Contains('"') || neutralized.Contains('\n') || neutralized.Contains('\r')
            ? $"\"{neutralized.Replace("\"", "\"\"")}\""
            : neutralized;
    }

}
