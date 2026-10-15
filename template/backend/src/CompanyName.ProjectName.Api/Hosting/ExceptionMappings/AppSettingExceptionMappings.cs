#if (Email || ExternalLogin)
using CompanyName.ProjectName.Application.Settings.Errors;
#endif
using Leistd.ExceptionHandling.Options;

namespace CompanyName.ProjectName.Api.Hosting.ExceptionMappings;

internal static class AppSettingExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
#if (Email)
        options.MapCode(AppSettingErrorCodes.TestEmailHostOnly, StatusCodes.Status403Forbidden);
        options.MapCode(AppSettingErrorCodes.TestEmailFailed, StatusCodes.Status503ServiceUnavailable);
        options.MapCode(AppSettingErrorCodes.EmailVerificationKeyMissing, StatusCodes.Status409Conflict);
#endif
#if (ExternalLogin)
        options.MapCode(AppSettingErrorCodes.ExternalLoginStatusHostOnly, StatusCodes.Status403Forbidden);
#endif
    }
}
