#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
#if (ExternalLogin)
using CompanyName.ProjectName.Domain.Users.Errors;
#endif
using Leistd.ExceptionHandling.Options;

namespace CompanyName.ProjectName.Api.Hosting.ExceptionMappings;

internal static class AuthExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        ApiExceptionMappings.Map(options, StatusCodes.Status401Unauthorized,
            AuthErrorCodes.InvalidCredentials,
            AuthErrorCodes.TwoFactorChallengeExpired,
            AuthErrorCodes.UserDisabled,
            AuthErrorCodes.UserLockedOut,
            AuthErrorCodes.UserTemporarilyLockedOut);
        ApiExceptionMappings.Map(options, StatusCodes.Status403Forbidden,
            AuthErrorCodes.RegistrationDisabled,
            AuthErrorCodes.TwoFactorSetupRequired,
            AuthErrorCodes.TwoFactorRequiredByPolicy);
        options.MapCode(AuthErrorCodes.PasskeyNotFound, StatusCodes.Status404NotFound);
        ApiExceptionMappings.Map(options, StatusCodes.Status409Conflict,
            AuthErrorCodes.CannotRevokeCurrentSession,
#if (Email)
            AuthErrorCodes.EmailAlreadyVerified,
#endif
            AuthErrorCodes.PasskeyAlreadyRegistered,
            AuthErrorCodes.TwoFactorAlreadyEnabled,
            AuthErrorCodes.TwoFactorNotEnabled);
#if (Email)
        options.MapCode(AuthErrorCodes.EmailVerificationUnavailable, StatusCodes.Status503ServiceUnavailable);
        options.MapCode(AuthErrorCodes.EmailCodeSendTooFrequent, StatusCodes.Status429TooManyRequests);
#endif
#if (ExternalLogin)
        ApiExceptionMappings.Map(options, StatusCodes.Status409Conflict,
            ExternalAuthErrorCodes.AccountExistsSignInToLink,
            ExternalAuthErrorCodes.AlreadyLinked,
            ExternalAuthErrorCodes.EmailOwnedByDeletedAccount,
            ExternalAuthErrorCodes.LastSignInMethod,
            ExternalAuthErrorCodes.ProviderAlreadyLinked,
            ExternalAuthErrorCodes.UsernameAllocationFailed,
            SecurityErrorCodes.PasswordAlreadySet);
        options.MapCode(ExternalAuthErrorCodes.ProviderNotConfigured, StatusCodes.Status503ServiceUnavailable);
#endif
    }
}
#endif
