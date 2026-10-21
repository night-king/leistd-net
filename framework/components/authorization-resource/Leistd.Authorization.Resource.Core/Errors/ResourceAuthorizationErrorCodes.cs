namespace Leistd.Authorization.Resource.Errors;

/// <summary>资源授权组件发出的稳定业务错误码。</summary>
public static class ResourceAuthorizationErrorCodes
{
    /// <summary>资源授权输入无效。</summary>
    public const string InvalidGrant = "ResourceAuthorization:InvalidGrant";

    /// <summary>资源授权版本冲突。</summary>
    public const string ConcurrencyConflict = "ResourceAuthorization:ConcurrencyConflict";
}
