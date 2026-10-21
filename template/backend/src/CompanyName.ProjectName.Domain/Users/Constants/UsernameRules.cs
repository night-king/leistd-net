namespace CompanyName.ProjectName.Domain.Users.Constants;

/// <summary>用户名的字符集与长度规则。</summary>
/// <remarks>
/// API 入口的校验与外部登录的用户名生成共用这一处：两边必须给出同一个答案，
/// 否则会出现"生成了一个用户自己改不回去的名字"或"注册得过、生成得不过"的分叉。
/// </remarks>
public static class UsernameRules
{
    /// <summary>允许的字符：字母、数字、下划线。</summary>
    public const string Pattern = "^[a-zA-Z0-9_]+$";

    /// <summary>最短长度。</summary>
    public const int MinLength = 3;

    /// <summary>最长长度。</summary>
    public const int MaxLength = 64;
}
