namespace Leistd.Authorization.Constants;

/// <summary>授权策略名的书写约定。</summary>
public static class PermissionPolicyNames
{
    /// <summary>“任一满足”策略名的分隔符，如 <c>"App.A|App.B"</c>；权限名不得包含该字符。</summary>
    public const char AnyOfSeparator = '|';
}
