using System.Collections.Concurrent;
using System.Reflection;
using Castle.DynamicProxy;
using Leistd.UnitOfWork.Attributes;
using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.DynamicProxy.Interceptors;

namespace Leistd.UnitOfWork.Interceptors;

/// <summary>为同步和异步方法建立声明式工作单元边界。</summary>
public class UnitOfWorkInterceptor : BaseAsyncInterceptor
{
    // 特性只取决于 MethodInfo，可按程序集生命周期缓存。
    private static readonly ConcurrentDictionary<MethodInfo, UnitOfWorkAttribute?> AttributeCache = new();

    private readonly UnitOfWorkOptions _unitOfWorkOptions;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ILogger<UnitOfWorkInterceptor>? _logger;

    /// <summary>创建工作单元拦截器。<c>ILogger</c> 为可选依赖，未注册时不记日志。</summary>
    public UnitOfWorkInterceptor(
        IOptions<UnitOfWorkOptions> unitOfWorkOptions,
        IUnitOfWorkManager unitOfWorkManager,
        ILogger<UnitOfWorkInterceptor>? logger = null)
    {
        _unitOfWorkOptions = unitOfWorkOptions.Value;
        _unitOfWorkManager = unitOfWorkManager;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Task InterceptAsync(
        IInvocation invocation,
        IInvocationProceedInfo proceedInfo,
        Func<IInvocation, IInvocationProceedInfo, Task> proceed)
        => RunAsync(invocation, async () =>
        {
            await proceed(invocation, proceedInfo);
            return default(object?);
        });

    /// <inheritdoc />
    protected override Task<TResult> InterceptAsync<TResult>(
        IInvocation invocation,
        IInvocationProceedInfo proceedInfo,
        Func<IInvocation, IInvocationProceedInfo, Task<TResult>> proceed)
        => RunAsync(invocation, () => proceed(invocation, proceedInfo));

    // CompleteAsync 在作用域存活期执行 AfterCommit；finally 负责释放作用域。
    // 提交使用方法声明的第一个 CancellationToken 参数（Web 请求里即 RequestAborted）：BeforeCommit 处理器拿到同一个令牌，
    // 提交前因它而起的取消按调用方取消处理，而不是记成提交失败。回滚不响应取消。
    private async Task<TResult> RunAsync<TResult>(IInvocation invocation, Func<Task<TResult>> proceed)
    {
        var method = GetMethodInfo(invocation);
        var attribute = GetUnitOfWorkAttribute(method);

        if (attribute is null || attribute.IsDisabled)
        {
            return await proceed();
        }

        var unitOfWorkOptions = attribute.CreateOptionsFromDefault(_unitOfWorkOptions);

        _logger?.LogDebug("Intercepting method {Method}; starting a unit of work", method.Name);

        var uow = _unitOfWorkManager.Begin(unitOfWorkOptions, requiresNew: false);

        try
        {
            var result = await proceed();
            await uow.CompleteAsync(GetCancellationToken(invocation, method));

            _logger?.LogDebug("Method {Method} completed; unit of work committed", method.Name);

            return result;
        }
        catch
        {
            // AfterCommit 失败时工作单元已提交，此时回滚是幂等空操作。
            await uow.RollbackAsync();
            throw;
        }
        finally
        {
            uow.Dispose();
        }
    }

    // 按声明的参数类型取，而不是按运行期值：object 参数里装箱的令牌不是这个方法的取消令牌
    private static CancellationToken GetCancellationToken(IInvocation invocation, MethodInfo method)
    {
        var parameters = method.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].ParameterType == typeof(CancellationToken))
                return invocation.Arguments[index] is CancellationToken token ? token : CancellationToken.None;
        }
        return CancellationToken.None;
    }

    private MethodInfo GetMethodInfo(IInvocation invocation)
    {
        return invocation.MethodInvocationTarget ?? invocation.GetConcreteMethod();
    }

    // 仅缓存特性；每次调用都需要独立的可变选项实例。
    private UnitOfWorkAttribute? GetUnitOfWorkAttribute(MethodInfo methodInfo)
    {
        return AttributeCache.GetOrAdd(methodInfo, static method =>
            method.GetCustomAttributes(true).OfType<UnitOfWorkAttribute>().FirstOrDefault()
            ?? method.DeclaringType?.GetTypeInfo()
                .GetCustomAttributes(true).OfType<UnitOfWorkAttribute>().FirstOrDefault());
    }
}
