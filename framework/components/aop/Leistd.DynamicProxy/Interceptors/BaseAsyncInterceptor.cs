using Castle.DynamicProxy;

namespace Leistd.DynamicProxy.Interceptors;

/// <summary>支持同步与异步方法的拦截器基类。</summary>
public abstract class BaseAsyncInterceptor : AsyncInterceptorBase
{
    /// <summary>拦截器执行顺序（数值越小越先执行，默认 0）。</summary>
    public virtual int Order => 0;
}
