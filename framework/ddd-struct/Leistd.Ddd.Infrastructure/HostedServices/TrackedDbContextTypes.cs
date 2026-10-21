namespace Leistd.Ddd.Infrastructure.HostedServices;

// 由 AddDddDbContext<TDbContext>() 登记的上下文类型，供启动期租户过滤器检查使用。
internal sealed class TrackedDbContextTypes
{
    private readonly HashSet<Type> _types = [];

    public IReadOnlyCollection<Type> Types => _types;

    public bool Add(Type dbContextType) => _types.Add(dbContextType);
}
