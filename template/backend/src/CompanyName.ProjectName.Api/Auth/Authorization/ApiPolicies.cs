namespace CompanyName.ProjectName.Api.Auth.Authorization;

/// <summary>与权限无关、只描述主体形态的授权策略名。</summary>
/// <remarks>
/// 权限点对应的策略名在 <c>PermissionConstant</c>；这里放的是"谁算一个可以操作自己数据的主体"。
/// 组件端点不接受隐式的默认策略（那会把宿主对默认主体的要求悄悄叠到每个端点上），
/// 因此把默认策略也以名字给出去，端点上看得见自己在要求什么。
/// </remarks>
public static class ApiPolicies
{
    /// <summary>当前登录主体：与默认策略同一份要求，用于读设置、读自己的权限、通知中心这类自用端点。</summary>
    public const string CurrentUser = "App.CurrentUser";
}
