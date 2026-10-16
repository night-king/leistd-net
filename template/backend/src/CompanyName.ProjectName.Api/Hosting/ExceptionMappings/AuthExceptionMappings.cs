#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
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
        ApiExceptionMappings.Map(options, StatusCodes.Status409Conflict,
            AuthErrorCodes.CannotRevokeCurrentSession,
#if (Email)
            AuthErrorCodes.EmailAlreadyVerified,
#endif
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
            ExternalAuthErrorCodes.UsernameAllocationFailed);
        options.MapCode(ExternalAuthErrorCodes.ProviderNotConfigured, StatusCodes.Status503ServiceUnavailable);
#endif
    }
}
#endif
