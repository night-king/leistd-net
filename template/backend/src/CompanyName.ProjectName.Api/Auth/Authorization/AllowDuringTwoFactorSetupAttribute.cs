#if (LocalIdentity)
namespace CompanyName.ProjectName.Api.Auth.Authorization;

/// <summary>受限会话（租户要求两步验证而本人尚未启用）也能调用的接口。</summary>
/// <remarks>
/// 名单刻意很短：取当前用户、完成两步验证设置、退出登录，以及界面启动必需的读取。
/// 允许匿名的接口本来就不依赖身份，不必再标。
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowDuringTwoFactorSetupAttribute : Attribute;
#endif
