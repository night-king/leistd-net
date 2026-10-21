using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Repositories;

namespace Leistd.Ddd.Infrastructure;

/// <summary>单个 DbContext 接入 DDD 基础设施时的仓储注册选项。</summary>
/// <remarks>
/// <para>不调用 <see cref="AddDefaultRepositories"/> 就不注册任何仓储，只需工作单元与租户过滤器闸门的上下文保持默认即可。</para>
/// <para>自定义仓储优先，默认注册跳过已有自定义仓储的实体，与调用顺序无关。</para>
/// </remarks>
public sealed class DddDbContextOptions
{
    internal bool RegisterDefaultRepositories { get; private set; }

    internal Dictionary<Type, Type> CustomRepositories { get; } = [];

    internal List<Type> ExplicitEntities { get; } = [];

    /// <summary>为本上下文按公开 <c>DbSet&lt;T&gt;</c> 声明的聚合根注册默认仓储。</summary>
    /// <remarks>
    /// 实体来源是公开 <c>DbSet&lt;T&gt;</c> 属性中实现 <see cref="IAggregateRoot"/> 的类型：仓储只为聚合根提供，
    /// 子实体可以声明 <c>DbSet</c>（让表名走命名约定）而不得到仓储，只能经根修改。
    /// 仅经模型配置映射的聚合根用 <see cref="AddDefaultRepository{TEntity}"/> 显式登记。
    /// </remarks>
    public DddDbContextOptions AddDefaultRepositories()
    {
        RegisterDefaultRepositories = true;
        return this;
    }

    /// <summary>为 <typeparamref name="TEntity"/> 注册自定义仓储实现，优先于默认注册。</summary>
    /// <remarks>
    /// 除默认仓储接口外，<typeparamref name="TImplementation"/> 实现的、派生自 <see cref="IRepository{TEntity}"/>
    /// 的自定义接口（如 <c>IUserRepository : IRepository&lt;User, Guid&gt;</c>）也注册为同一实现。
    /// </remarks>
    public DddDbContextOptions AddRepository<TEntity, TImplementation>()
        where TEntity : class, IEntity
        where TImplementation : class, IRepository<TEntity>
    {
        CustomRepositories[typeof(TEntity)] = typeof(TImplementation);
        return this;
    }

    /// <summary>点名为 <typeparamref name="TEntity"/> 注册默认仓储，用于未暴露 <c>DbSet&lt;T&gt;</c> 的聚合根。</summary>
    public DddDbContextOptions AddDefaultRepository<TEntity>()
        where TEntity : class, IEntity
    {
        ExplicitEntities.Add(typeof(TEntity));
        return this;
    }
}
