namespace Leistd.Ddd.Domain.Entities;

/// <summary>标记具有乐观并发标记的实体。</summary>
/// <remarks>
/// 基础设施的实体约定（<c>DddEntityConvention</c>）把本属性配成必填的 EF 并发令牌，更新语句自动带
/// <c>WHERE ConcurrencyStamp = @原值</c>，落败方得到 <c>DbUpdateConcurrencyException</c>。
/// 初值写 <see cref="ConcurrencyStamps.New"/>，漏写时由拦截器在插入前补种。
/// 仅覆盖经变更跟踪器的读改写；断开连接的更新见 ddd-struct 组件文档。
/// </remarks>
public interface IHasConcurrencyStamp
{
    /// <summary>并发标记；写入时由基础设施自动换发（见 <c>ConcurrencyStampSaveChangesInterceptor</c>）。</summary>
    string ConcurrencyStamp { get; set; }
}

/// <summary>并发标记生成入口；换发由 <c>ConcurrencyStampSaveChangesInterceptor</c> 统一完成。</summary>
public static class ConcurrencyStamps
{
    /// <summary>生成新的并发标记。</summary>
    public static string New() => Guid.NewGuid().ToString("N");
}
