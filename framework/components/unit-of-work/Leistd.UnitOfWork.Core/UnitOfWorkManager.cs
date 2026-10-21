using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.UnitOfWork;

/// <inheritdoc/>
public class UnitOfWorkManager(
    IOptions<UnitOfWorkOptions> defaultUowOptions,
    IServiceProvider serviceProvider,
    IAmbientUnitOfWork ambientUnitOfWork,
    ILogger<UnitOfWorkManager>? logger = null) : IUnitOfWorkManager
{
    /// <inheritdoc />
    public IUnitOfWork? Current => GetCurrentUnitOfWork();

    /// <inheritdoc />
    public IUnitOfWork Begin(UnitOfWorkOptions? options = null, bool requiresNew = false)
    {
        if (options is not null)
        {
            var failures = UnitOfWorkOptionsValidator.GetFailures(options, nameof(UnitOfWorkOptions) + ".");
            if (failures.Count > 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), string.Join(" ", failures));
            }
        }

        // 新建工作单元前定案本次选项，避免修改共享的默认实例。
        var effectiveOptions = options?.Clone() ?? defaultUowOptions.Value.Clone();
        var currentUow = GetCurrentUnitOfWork();
        if (currentUow != null && !requiresNew)
        {
            logger?.LogDebug("Reusing existing unit of work {UowId} (creating a child unit of work)", currentUow.Id);
            return new ChildUnitOfWork(currentUow);
        }

        var unitOfWork = CreateNewUnitOfWork(effectiveOptions);

        logger?.LogDebug("Created new unit of work {UowId}", unitOfWork.Id);

        return unitOfWork;
    }

    private IUnitOfWork? GetCurrentUnitOfWork()
    {
        var uow = ambientUnitOfWork.Get();

        while (uow != null && (uow.IsDisposed || uow.IsCompleted))
        {
            uow = uow.Outer;
        }

        return uow;
    }

    private IUnitOfWork CreateNewUnitOfWork(UnitOfWorkOptions effectiveOptions)
    {
        var scope = serviceProvider.CreateScope();
        try
        {
            var outerUow = ambientUnitOfWork.Get();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // 在发布 ambient 前初始化；失败时由本层回收作用域，不暴露失败实例。
            unitOfWork.SetOuter(outerUow);
            unitOfWork.Initialize(effectiveOptions);

            // 按接口订阅释放事件，使自定义实现也能恢复外层环境并回收作用域。
            unitOfWork.Disposed += (sender, args) =>
            {
                ambientUnitOfWork.Set(outerUow);
                scope.Dispose();
            };

            // 全部就绪后才发布：在此之前失败的话，外层 ambient 从未被覆盖过。
            ambientUnitOfWork.Set(unitOfWork);
            return unitOfWork;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
