namespace CompanyName.ProjectName.Domain.Auth.Errors;

/// <summary>ExternalAuth 业务错误码。</summary>
public static class ExternalAuthErrorCodes
{
    public const string AccountExistsSignInToLink = "ExternalAuth:AccountExistsSignInToLink";
    public const string AlreadyLinked = "ExternalAuth:AlreadyLinked";
    public const string EmailOwnedByDeletedAccount = "ExternalAuth:EmailOwnedByDeletedAccount";
    public const string InvalidState = "ExternalAuth:InvalidState";
    public const string LastSignInMethod = "ExternalAuth:LastSignInMethod";
    public const string ProviderAlreadyLinked = "ExternalAuth:ProviderAlreadyLinked";
    public const string ProviderNotConfigured = "ExternalAuth:ProviderNotConfigured";
    public const string UsernameAllocationFailed = "ExternalAuth:UsernameAllocationFailed";
}
