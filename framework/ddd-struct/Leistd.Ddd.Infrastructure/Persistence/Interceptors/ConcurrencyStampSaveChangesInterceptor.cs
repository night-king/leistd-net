using Leistd.Ddd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Leistd.Ddd.Infrastructure.Persistence.Interceptors;

/// <summary>在保存前初始化或换发实体的并发标记。</summary>
/// <remarks>
/// 领域代码只需给初值；漏写时插入前补种，使列不会带着 <see langword="null"/> 落库把并发校验废掉。
/// 删除不动：它的校验用的正是读出来的原值。
/// </remarks>
public sealed class ConcurrencyStampSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyStamps(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyStamps(eventData.Context);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    private static void ApplyStamps(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IHasConcurrencyStamp)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    SeedIfMissing(entry);
                    break;
                case EntityState.Modified:
                    Renew(entry);
                    break;
            }
        }
    }

    private static void SeedIfMissing(EntityEntry entry)
    {
        var property = entry.Property(nameof(IHasConcurrencyStamp.ConcurrencyStamp));

        if (string.IsNullOrWhiteSpace(property.CurrentValue as string))
        {
            property.CurrentValue = ConcurrencyStamps.New();
        }
    }

    private static void Renew(EntityEntry entry)
    {
        // 写 CurrentValue 而不是碰 OriginalValue：后者是 EF 构造 WHERE 子句的依据，
        // 改它等于把并发校验对准一个错误的目标
        entry.Property(nameof(IHasConcurrencyStamp.ConcurrencyStamp)).CurrentValue =
            ConcurrencyStamps.New();
    }
}
