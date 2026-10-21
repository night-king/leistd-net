# 动态代理拦截器基类

`BaseAsyncInterceptor` 基于 Castle DynamicProxy 统一同步与异步方法的横切逻辑，`Order` 决定多个拦截器的顺序。

## 何时使用

| 场景 | 做法 |
| --- | --- |
| 需要为服务方法织入事务、追踪、日志等横切逻辑 | 继承 `BaseAsyncInterceptor` 编写拦截器 |
| 一个服务上叠加多个拦截器，需控制执行先后 | 重写 `Order`（值越小越外层、越先执行） |
| 只想使用框架已内置的拦截能力（追踪 / 工作单元） | 无需直接引用本包，改用对应组件 |

> 本包只提供拦截器基类；织入与代理生成由[依赖注入](./dependency-injection.md)组件完成，本包不含 DI 扩展方法。

## 安装

```bash
# 编写自定义拦截器时引用（框架内置拦截器组件已传递引用，通常无需单独添加）
dotnet add package Leistd.DynamicProxy
```

## 使用

继承 `BaseAsyncInterceptor`，重写两个 `InterceptAsync` 重载（分别对应无返回值与有返回值的方法），在调用 `proceed` 前后插入横切逻辑。构造函数可正常注入依赖：

```csharp
using System.Diagnostics;
using Castle.DynamicProxy;
using Leistd.DynamicProxy.Interceptors;
using Microsoft.Extensions.Logging;

public class TimingInterceptor(ILogger<TimingInterceptor> logger) : BaseAsyncInterceptor
{
    public override int Order => 100;

    protected override async Task InterceptAsync(
        IInvocation invocation,
        IInvocationProceedInfo proceedInfo,
        Func<IInvocation, IInvocationProceedInfo, Task> proceed)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await proceed(invocation, proceedInfo);
        }
        finally
        {
            logger.LogInformation("{Method} 耗时 {Ms}ms",
                invocation.Method.Name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    protected override async Task<TResult> InterceptAsync<TResult>(
        IInvocation invocation,
        IInvocationProceedInfo proceedInfo,
        Func<IInvocation, IInvocationProceedInfo, Task<TResult>> proceed)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await proceed(invocation, proceedInfo);
        }
        finally
        {
            logger.LogInformation("{Method} 耗时 {Ms}ms",
                invocation.Method.Name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}
```

拦截器写好后，由 [依赖注入](./dependency-injection.md) 组件负责把它注册并织入目标服务（内部用 `IProxyGenerator` 生成接口/类代理，并按各拦截器的 `Order` 升序织入）。

## 接口参考

`Leistd.DynamicProxy.Interceptors` 命名空间：

| 成员 | 说明 |
| --- | --- |
| `BaseAsyncInterceptor` | 同时支持同步与异步方法的拦截器基类 |
| `BaseAsyncInterceptor.Order` | `virtual int`，拦截器执行顺序；**数值越小越先执行（越靠外层）**，默认 `0` |
| `InterceptAsync(invocation, proceedInfo, proceed)` | 来自基类，需重写；拦截**无返回值**方法，调用 `proceed(...)` 执行原方法 |
| `InterceptAsync<TResult>(invocation, proceedInfo, proceed)` | 来自基类，需重写；拦截**有返回值**方法，返回原方法结果 |

## 注意事项

- `Order` 数值越小越先执行（越靠外层），可为负数。
- 必须重写两个 `InterceptAsync` 重载：有返回值的方法走泛型重载，无返回值的方法走非泛型重载，二者逻辑通常一致，需分别实现。
- 不要忘记在两个重载里都调用 `proceed(invocation, proceedInfo)`；不调用则原方法不会执行。
- 类代理要求被拦截方法为 `virtual`，接口代理无此限制。

## 相关

- [依赖注入](./dependency-injection.md)
- [链路追踪](./tracing.md)
- [工作单元](./unit-of-work.md)
