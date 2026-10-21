namespace Leistd.MultiTenancy.Tenancy;

/// <summary>标记需要按 <see cref="TenantId"/> 隔离的实体。</summary>
/// <remarks>
/// <see cref="TenantId"/> 为 <c>null</c> 表示宿主（Host）数据——同一实体类型同时服务两侧。
/// 实现该接口的实体会被 DDD 基座的全局查询过滤器按当前租户过滤，并在进入变更跟踪时由
/// <c>BaseDbContext</c> 填充当前租户 Id。
/// 宿主侧专属实体（如租户注册表本身）不应实现该接口。
/// </remarks>
public interface IMultiTenant
{
    /// <summary>所属租户标识；<see langword="null"/> 表示宿主数据。</summary>
    Guid? TenantId { get; }
}
