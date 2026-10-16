#if (LocalIdentity)
namespace CompanyName.ProjectName.Domain.Users.Errors;

/// <summary>Security 业务错误码：账号凭据（口令）相关。</summary>
public static class SecurityErrorCodes
{
    public const string CurrentPasswordIncorrect = "Security:CurrentPasswordIncorrect";
    public const string LocalPasswordNotSet = "Security:LocalPasswordNotSet";
#if (ExternalLogin)
    public const string PasswordAlreadySet = "Security:PasswordAlreadySet";
#endif
    public const string PasswordRequired = "Security:PasswordRequired";
    public const string PasswordTooShort = "Security:PasswordTooShort";
    public const string PasswordTooLong = "Security:PasswordTooLong";
}
#endif
