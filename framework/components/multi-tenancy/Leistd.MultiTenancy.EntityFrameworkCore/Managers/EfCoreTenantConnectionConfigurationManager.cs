using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Leistd.Timing;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.EntityFrameworkCore.Extensions;
using Leistd.MultiTenancy.Exceptions;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Managers;

/// <summary><see cref="ITenantConnectionConfigurationManager"/> 的 EF Core 实现。</summary>
/// <remarks>
/// 时间由本类填充，不依赖控制面 DbContext 接入审计层；<c>CreatorId</c> / <c>LastModifierId</c> 由宿主审计层补充，
/// 需要控制面 DbContext 同时接上创建审计钩子与 <c>AuditSaveChangesInterceptor</c>。
/// 连接串经宿主的 Data Protection 密钥环加密后落库。
/// </remarks>
public class EfCoreTenantConnectionConfigurationManager<TDbContext> : ITenantConnectionConfigurationManager
    where TDbContext : DbContext
{
    private readonly IDbContextProvider<TDbContext> _dbContextProvider;
    private readonly IClock _clock;
    private readonly TenantConnectionStringProtector _protector;

    /// <summary>创建管理器。</summary>
    /// <param name="dbContextProvider">工作单元内的 DbContext 提供器。</param>
    /// <param name="clock">时钟。</param>
    /// <param name="dataProtectionProvider">宿主的 Data Protection 提供器，用于加密连接串。</param>
    public EfCoreTenantConnectionConfigurationManager(
        IDbContextProvider<TDbContext> dbContextProvider,
        IClock clock,
        IDataProtectionProvider dataProtectionProvider)
    {
        _dbContextProvider = dbContextProvider;
        _clock = clock;
        _protector = new TenantConnectionStringProtector(dataProtectionProvider);
    }

    /// <inheritdoc />
    public async Task<TenantConnectionConfiguration> SetAsync(
        Guid tenantId,
        string name,
        string connectionString,
        long? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = TenantConnectionNames.NormalizeInput(name);
        var normalizedConnectionString = TenantConnectionStrings.NormalizeInput(connectionString);
        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);

        var tenant = await LoadTenantAsync(dbContext, tenantId, cancellationToken);
        var record = await dbContext.Set<TenantConnectionRecord>()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Name == normalizedName, cancellationToken);

        if (expectedVersion != record?.Version)
        {
            throw new TenantConnectionVersionConflictException(tenantId, expectedVersion, record?.Version);
        }

        // 判据是本次写入是否改变已有数据的物理落点；IsActive 在前短路，停用态不必再查连接表。
        if (tenant.IsActive
            && await ChangesDataResidencyAsync(dbContext, tenantId, record, cancellationToken))
        {
            throw new TenantConnectionChangeRequiresInactiveTenantException(tenantId);
        }

        tenant.Version++;

        var isFirstWrite = record is null;
        var now = _clock.Normalize(_clock.Now);
        if (record is null)
        {
            record = new TenantConnectionRecord
            {
                TenantId = tenantId,
                Name = normalizedName,
                Version = 1,
                CreationTime = now
            };
            dbContext.Set<TenantConnectionRecord>().Add(record);
        }
        else
        {
            record.Version++;
            record.LastModificationTime = now;
        }

        // 只有密文进入变更跟踪与数据库；即使开启 EF 敏感数据日志，参数里也只有密文
        record.ProtectedConnectionString = _protector.Protect(normalizedConnectionString);

        await SaveAsync(dbContext, tenantId, normalizedName, record, tenant, expectedVersion, isFirstWrite, cancellationToken);

        return new TenantConnectionConfiguration
        {
            TenantId = record.TenantId,
            Name = record.Name,
            ConnectionString = normalizedConnectionString,
            Version = record.Version
        };
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        Guid tenantId,
        string name,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = TenantConnectionNames.NormalizeInput(name);
        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);

        var tenant = await LoadTenantAsync(dbContext, tenantId, cancellationToken);
        var record = await dbContext.Set<TenantConnectionRecord>()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Name == normalizedName, cancellationToken);

        // 行不存在也走版本冲突：调用方以为自己在删一条读到过的登记，实际它已经没了
        if (record is null || record.Version != expectedVersion)
        {
            throw new TenantConnectionVersionConflictException(tenantId, expectedVersion, record?.Version);
        }

        // 删除等同于把该名字改回"用服务自己的库"，同样要先停用并排空
        if (tenant.IsActive)
        {
            throw new TenantConnectionChangeRequiresInactiveTenantException(tenantId);
        }

        tenant.Version++;
        dbContext.Set<TenantConnectionRecord>().Remove(record);

        await SaveAsync(dbContext, tenantId, normalizedName, record, tenant, expectedVersion, isFirstWrite: false, cancellationToken);
    }

    private static async Task<TenantRecord> LoadTenantAsync(
        TDbContext dbContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // 推进租户版本，防止验证后被并发激活
        var tenant = await dbContext.UndeletedTenants()
            .FirstOrDefaultAsync(x => x.Id == tenantId, cancellationToken);

        return tenant ?? throw new TenantNotFoundException(tenantId.ToString());
    }

    // 本次写入是否改变已有数据的物理落点（只有这类写入要求先停用），按该租户此前有无任意登记判断：
    // 改已有的行会换库；无任何登记时首条登记把它变为分库租户，既有数据与管理员留在原库——两者都要停用。
    // 已是分库租户补一个缺失的名字不搁浅数据（该服务此前失败关闭，见 TenantConnectionTargets.Select），放行。
    private static async Task<bool> ChangesDataResidencyAsync(
        TDbContext dbContext,
        Guid tenantId,
        TenantConnectionRecord? record,
        CancellationToken cancellationToken)
    {
        if (record is not null)
        {
            return true;
        }

        return !await dbContext.Set<TenantConnectionRecord>()
            .AnyAsync(x => x.TenantId == tenantId, cancellationToken);
    }

    private async Task SaveAsync(
        TDbContext dbContext,
        Guid tenantId,
        string normalizedName,
        TenantConnectionRecord record,
        TenantRecord tenant,
        long? expectedVersion,
        bool isFirstWrite,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception) when (InvolvesThisTenantRow(exception))
        {
            // 租户行冲突表示生命周期已变，不是连接版本过期。
            DiscardOwnChanges();
            throw new TenantConcurrencyConflictException(tenantId);
        }
        catch (DbUpdateException exception) when (isFirstWrite && exception is not DbUpdateConcurrencyException)
        {
            // 仅在重读确认并发首创后转换异常，避免误报其他写入错误。
            DiscardOwnChanges();

            var existing = await dbContext.Set<TenantConnectionRecord>()
                .AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId && x.Name == normalizedName, cancellationToken);
            if (!existing)
            {
                throw;
            }

            throw new TenantConcurrencyConflictException(tenantId);
        }
        catch (DbUpdateConcurrencyException exception) when (InvolvesThisConnectionRow(exception))
        {
            // 只转换本连接行的并发失败，不掩盖同次保存中其他实体的冲突。
            DiscardOwnChanges();

            var actual = await dbContext.Set<TenantConnectionRecord>()
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Name == normalizedName)
                .Select(x => (long?)x.Version)
                .FirstOrDefaultAsync(cancellationToken);

            throw new TenantConnectionVersionConflictException(tenantId, expectedVersion, actual, exception);
        }

        // 仅恢复本操作的行，保留宿主工作单元中的其他待提交更改。
        void DiscardOwnChanges()
        {
            var connectionEntry = dbContext.Entry(record);
            connectionEntry.State = connectionEntry.State switch
            {
                EntityState.Added => EntityState.Detached,
                EntityState.Modified or EntityState.Deleted => Restore(connectionEntry),
                _ => connectionEntry.State
            };

            // 租户行保持跟踪，仅恢复本次推进的版本。
            var tenantEntry = dbContext.Entry(tenant);
            if (tenantEntry.State == EntityState.Modified)
            {
                Restore(tenantEntry);
            }

            static EntityState Restore(EntityEntry entry)
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
                return EntityState.Unchanged;
            }
        }

        bool InvolvesThisTenantRow(DbUpdateConcurrencyException exception) =>
            exception.Entries.Any(entry =>
                entry.Entity is TenantRecord candidate && candidate.Id == tenantId);

        bool InvolvesThisConnectionRow(DbUpdateConcurrencyException exception) =>
            exception.Entries.Any(entry =>
                entry.Entity is TenantConnectionRecord candidate
                && candidate.TenantId == tenantId
                && candidate.Name == normalizedName);
    }
}
