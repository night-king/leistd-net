using Leistd.Data.Paging;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Leistd.Timing;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.EntityFrameworkCore.Extensions;
using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.EntityFrameworkCore.Stores;
using Leistd.MultiTenancy.Exceptions;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Managers;

/// <summary>使用 EF Core 持久化租户生命周期和并发版本。</summary>
/// <remarks>
/// 管理器自行 SaveChanges；在外层工作单元事务内调用时仅表现为提前刷写，不破坏事务边界。
/// 软删除与创建时间由管理器自己落定，控制面上下文无需继承 <c>BaseDbContext</c>；<c>CreatorId</c> / <c>DeleterId</c> 仍归审计层填充。
/// 无缓存失效步骤：存储直读库，启停与删除提交即生效。
/// </remarks>
public class EfCoreTenantManager<TDbContext>(
    IDbContextProvider<TDbContext> dbContextProvider,
    ITenantNormalizer normalizer,
    IClock clock) : ITenantManager
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public async Task<TenantConfiguration> CreateAsync(
        string name,
        string? displayName,
        bool isActive,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        TenantNames.EnsureValid(name);
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        var normalizedName = normalizer.NormalizeName(name)!;
        await EnsureNameNotTakenAsync(dbContext, normalizedName, excludeId: null, cancellationToken);

        var record = new TenantRecord
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            NormalizedName = normalizedName,
            DisplayName = displayName,
            Description = description,
            IsActive = isActive,
            CreationTime = clock.Normalize(clock.Now)
        };

        var entry = dbContext.Set<TenantRecord>().Add(record);
        await SaveTenantAsync(dbContext, record.Id, entry, normalizedName, cancellationToken);
        return EfCoreTenantStore<TDbContext>.ToConfiguration(record);
    }

    /// <inheritdoc />
    public async Task<TenantConfiguration> UpdateAsync(
        Guid id,
        string name,
        string? displayName,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        var record = await GetAsync(dbContext, id, cancellationToken);
        // 名称变了才校验（只改大小写也算改名），存量名称可能不符合当前规则
        if (!string.Equals(record.Name, name, StringComparison.Ordinal))
        {
            TenantNames.EnsureValid(name);
        }

        var normalizedName = normalizer.NormalizeName(name)!;

        if (!string.Equals(record.NormalizedName, normalizedName, StringComparison.Ordinal))
        {
            await EnsureNameNotTakenAsync(dbContext, normalizedName, excludeId: id, cancellationToken);
        }

        record.Name = name;
        record.NormalizedName = normalizedName;
        record.DisplayName = displayName;
        record.Description = description;
        record.Version++;

        await SaveTenantAsync(dbContext, id, dbContext.Entry(record), normalizedName, cancellationToken);
        return EfCoreTenantStore<TDbContext>.ToConfiguration(record);
    }

    /// <inheritdoc />
    public async Task<TenantConfiguration> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var record = await GetAsync(dbContext, id, cancellationToken);
        record.IsActive = isActive;
        record.Version++;

        await SaveTenantAsync(dbContext, id, dbContext.Entry(record), normalizedName: null, cancellationToken);
        return EfCoreTenantStore<TDbContext>.ToConfiguration(record);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var record = await GetAsync(dbContext, id, cancellationToken);

        // 显式落软删除字段，不依赖宿主的审计拦截器。
        record.IsDeleted = true;
        record.DeletionTime = clock.Normalize(clock.Now);
        record.Version++;

        await SaveTenantAsync(dbContext, id, dbContext.Entry(record), normalizedName: null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var record = await dbContext.UndeletedTenants()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return record is null ? null : EfCoreTenantStore<TDbContext>.ToConfiguration(record);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TenantConfiguration>> GetPagedAsync(
        string? keyword,
        bool? isActive,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var query = dbContext.UndeletedTenants().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = normalizer.NormalizeName(keyword)!;
            query = query.Where(t =>
                t.NormalizedName.Contains(normalizedKeyword) ||
                (t.DisplayName != null && t.DisplayName.Contains(keyword)));
        }

        if (isActive is { } active)
        {
            query = query.Where(t => t.IsActive == active);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(t => t.CreationTime)
            .ThenByDescending(t => t.Id)
            .Skip(page.Offset)
            .Take(page.Limit)
            .ToListAsync(cancellationToken);

        return new PagedResult<TenantConfiguration>(total, items.Select(EfCoreTenantStore<TDbContext>.ToConfiguration));
    }

    // 先映射更具体的版本冲突，再确认名称唯一性冲突；其他数据库异常原样上抛。
    // normalizedName 为空表示本次写入不参与名称竞争。
    private static async Task SaveTenantAsync(
        TDbContext dbContext,
        Guid tenantId,
        EntityEntry<TenantRecord> entry,
        string? normalizedName,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception) when (exception.Entries.Any(failed =>
            failed.Entity is TenantRecord candidate && candidate.Id == tenantId))
        {
            // 只映射当前租户行的冲突，保留同次保存中其他实体的并发异常。
            DiscardPendingChange(entry);
            throw new TenantConcurrencyConflictException(tenantId);
        }
        catch (DbUpdateException) when (normalizedName is not null)
        {
            // 只恢复本次条目，避免丢弃宿主的其他待写实体。
            DiscardPendingChange(entry);

            // 从存储确认名称已被其他租户占用，避免把其他约束错误映射为名称冲突。
            var takenByOther = await dbContext.UndeletedTenants()
                .AsNoTracking()
                .AnyAsync(
                    t => t.NormalizedName == normalizedName && t.Id != entry.Entity.Id,
                    cancellationToken);

            if (takenByOther)
            {
                throw new DuplicateTenantNameException(normalizedName);
            }

            // 其他约束或数据库故障不能冒充名称冲突。
            throw;
        }
    }

    private static void DiscardPendingChange(EntityEntry<TenantRecord> entry)
    {
        switch (entry.State)
        {
            case EntityState.Added:
                entry.State = EntityState.Detached;
                break;
            case EntityState.Modified:
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
                break;
        }
    }

    private static async Task<TenantRecord> GetAsync(
        TDbContext dbContext,
        Guid id,
        CancellationToken cancellationToken)
    {
        return await dbContext.UndeletedTenants()
                   .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
               ?? throw new TenantNotFoundException(id.ToString());
    }

    private static async Task EnsureNameNotTakenAsync(
        TDbContext dbContext,
        string normalizedName,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var taken = await dbContext.UndeletedTenants()
            .AnyAsync(t => t.NormalizedName == normalizedName && (excludeId == null || t.Id != excludeId),
                cancellationToken);

        if (taken)
        {
            throw new DuplicateTenantNameException(normalizedName);
        }
    }

}
