using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.EntityFrameworkCore.Extensions;
using Leistd.MultiTenancy.Stores;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Stores;

/// <summary>使用 EF Core 直接查询租户配置。</summary>
/// <remarks>
/// 不缓存，每次解析都读库：<see cref="TenantConfiguration.IsActive"/> 是访问控制状态，直读让停用与删除在提交时对所有节点生效。
/// 成本是每请求一次索引查找，复用当前请求的 <typeparamref name="TDbContext"/>。确需缓存时自行包装 <see cref="ITenantStore"/> 装饰器。
/// </remarks>
public class EfCoreTenantStore<TDbContext>(IDbContextProvider<TDbContext> dbContextProvider) : ITenantStore
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public async Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await (await QueryAsync(cancellationToken))
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        return record is null ? null : ToConfiguration(record);
    }

    /// <inheritdoc />
    public async Task<TenantConfiguration?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        var record = await (await QueryAsync(cancellationToken))
            .FirstOrDefaultAsync(t => t.NormalizedName == normalizedName, cancellationToken);
        return record is null ? null : ToConfiguration(record);
    }

    private async Task<IQueryable<TenantRecord>> QueryAsync(CancellationToken cancellationToken)
        => (await dbContextProvider.GetDbContextAsync(cancellationToken))
            .UndeletedTenants().AsNoTracking();

    internal static TenantConfiguration ToConfiguration(TenantRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        NormalizedName = record.NormalizedName,
        DisplayName = record.DisplayName,
        Description = record.Description,
        IsActive = record.IsActive,
        CreationTime = record.CreationTime
    };
}
