using Leistd.Data.Paging;
using Leistd.OperationRecords.Dtos;
using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.Queries;

/// <summary>操作记录的查询、筛选项与导出用例：按当前读者裁剪可见范围与字段。</summary>
/// <remarks>
/// <para>无租户上下文即宿主读者，看得到全部可见层与仅宿主字段；租户读者看不到
/// <see cref="OperationVisibility.Host"/> 层，<see cref="OperationVisibility.Actor"/> 层只看本人的。分页与导出共用同一判定。</para>
/// <para>不做权限判定：由端点授权策略把守；在别的入口直接调用时由调用方负责授权。</para>
/// <para>入参按 DataAnnotations 校验，失败抛 <see cref="System.ComponentModel.DataAnnotations.ValidationException"/>。</para>
/// </remarks>
public interface IOperationRecordQueryService
{
    /// <summary>按创建时间倒序分页查询当前读者可见的记录。</summary>
    /// <remarks>给了类别或动作筛选、但展开后没有一个对读者可见的动作时，返回空页而不是全量。</remarks>
    Task<PagedResult<OperationRecordOutputDto>> GetPagedListAsync(
        GetOperationRecordPagedInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>当前读者可见的类别与动作，用于驱动筛选框。</summary>
    Task<OperationRecordFilterOptionsOutputDto> GetFilterOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>导出当前读者可见的记录为 CSV，文件生成后记一条导出记录。</summary>
    /// <remarks>
    /// CSV 带 UTF-8 BOM；以 <c>= + - @</c> 等开头的字段前缀单引号防公式注入；仅宿主字段只在宿主导出时成列。
    /// </remarks>
    /// <param name="input">导出条件。</param>
    /// <param name="audit">导出本身的审计口径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<OperationRecordExportFileDto> ExportAsync(
        ExportOperationRecordsInputDto input,
        OperationRecordExportAudit audit,
        CancellationToken cancellationToken = default);
}
