namespace CompanyName.ProjectName.Domain.Auth.Errors;

/// <summary>Auth 业务错误码。</summary>
public static class AuthErrorCodes
{
    public const string CannotRevokeCurrentSession = "Auth:CannotRevokeCurrentSession";
    public const string CaptchaInvalid = "Auth:CaptchaInvalid";
#if (Email)
    public const string EmailAlreadyVerified = "Auth:EmailAlreadyVerified";
    public const string EmailCodeExpired = "Auth:EmailCodeExpired";
    public const string EmailCodeInvalid = "Auth:EmailCodeInvalid";
    public const string EmailCodeRequired = "Auth:EmailCodeRequired";
    public const string EmailCodeSendTooFrequent = "Auth:EmailCodeSendTooFrequent";
    public const string EmailVerificationDisabled = "Auth:EmailVerificationDisabled";
    public const string EmailVerificationUnavailable = "Auth:EmailVerificationUnavailable";
#endif
    public const string InvalidCredentials = "Auth:InvalidCredentials";
    public const string ReauthenticationMethodUnavailable = "Auth:ReauthenticationMethodUnavailable";
    public const string ReauthenticationRequired = "Auth:ReauthenticationRequired";
    public const string RegistrationDisabled = "Auth:RegistrationDisabled";
    public const string SignInAgainRequired = "Auth:SignInAgainRequired";
    public const string TwoFactorAlreadyEnabled = "Auth:TwoFactorAlreadyEnabled";
    public const string TwoFactorChallengeExpired = "Auth:TwoFactorChallengeExpired";
    public const string TwoFactorCodeInvalid = "Auth:TwoFactorCodeInvalid";
    public const string TwoFactorIndependentFactorRequired = "Auth:TwoFactorIndependentFactorRequired";
    public const string TwoFactorMethodUnavailable = "Auth:TwoFactorMethodUnavailable";
    public const string TwoFactorNotEnabled = "Auth:TwoFactorNotEnabled";
    public const string TwoFactorRequiredByPolicy = "Auth:TwoFactorRequiredByPolicy";
    public const string TwoFactorSetupExpired = "Auth:TwoFactorSetupExpired";
    public const string TwoFactorSetupRequired = "Auth:TwoFactorSetupRequired";
    public const string UserDisabled = "Auth:UserDisabled";
    public const string UserLockedOut = "Auth:UserLockedOut";
    public const string UserTemporarilyLockedOut = "Auth:UserTemporarilyLockedOut";
}
