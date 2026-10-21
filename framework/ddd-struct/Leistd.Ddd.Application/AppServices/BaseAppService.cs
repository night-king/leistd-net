using Leistd.Ddd.Application.Contracts.AppServices;

namespace Leistd.Ddd.Application.AppServices;

/// <summary>应用服务基类，同时承载 <see cref="IAppService"/> 约定标记。</summary>
/// <remarks>
/// 不提供服务定位器或自动注册。派生类通过构造函数声明依赖，显式注册服务；
/// 事务、授权等横切行为通过对应组件的特性与拦截器组合。
/// </remarks>
public abstract class BaseAppService : IAppService
{
}
