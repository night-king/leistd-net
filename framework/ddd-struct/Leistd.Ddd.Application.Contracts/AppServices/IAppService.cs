namespace Leistd.Ddd.Application.Contracts.AppServices;

/// <summary>应用服务的意图标记。</summary>
/// <remarks>
/// 仅表达类型职责，不参与 DI 注册或拦截器织入。服务需显式注册；
/// <c>BaseAppService</c> 已实现本接口，派生类无需重复声明。
/// </remarks>
public interface IAppService
{
}
