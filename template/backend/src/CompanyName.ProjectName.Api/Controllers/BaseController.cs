using Microsoft.AspNetCore.Mvc;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>控制器基类：只提供 <see cref="ApiControllerAttribute"/>。</summary>
/// <remarks>
/// <b>刻意不在这里声明 <c>[Route]</c>。</b>本项目的路由约定是显式版本化路径
/// （<c>api/v1/&lt;kebab-复数&gt;</c>，如 <c>api/v1/tenant-connections</c>），
/// 每个控制器自己声明；基类给一个 <c>api/[controller]</c> 之类的模板只会是死配置——
/// 派生类必然覆盖它，读者却会以为存在一条没人遵守的约定。
/// </remarks>
[ApiController]
public abstract class BaseController : ControllerBase
{
}
