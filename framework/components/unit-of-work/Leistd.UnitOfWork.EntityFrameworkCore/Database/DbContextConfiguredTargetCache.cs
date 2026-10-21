using System.Collections.Concurrent;

namespace Leistd.UnitOfWork.EntityFrameworkCore.Database;

// AddDbContext 配置在注册期固定，每种类型只需探测一次物理目标。
// 缓存必须属于宿主；静态状态会在同进程的多宿主间串用连接配置。
internal sealed class DbContextConfiguredTargetCache
{
    private readonly ConcurrentDictionary<Type, Lazy<string?>> _byDbContextType = new();

    // null 也会缓存，表示非关系型 provider 或未配置连接串。
    public string? GetOrProbe(Type dbContextType, Func<string?> probe)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);
        ArgumentNullException.ThrowIfNull(probe);

        return _byDbContextType
            .GetOrAdd(dbContextType, _ => new Lazy<string?>(probe, LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }
}
