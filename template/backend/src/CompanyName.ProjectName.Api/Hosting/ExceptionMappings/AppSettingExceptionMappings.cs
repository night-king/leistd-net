#if (Email)
using CompanyName.ProjectName.Application.Settings.Errors;
#endif
using Leistd.ExceptionHandling.Options;

namespace CompanyName.ProjectName.Api.Hosting.ExceptionMappings;

internal static class AppSettingExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
#if (Email)
        options.MapCode(AppSettingErrorCodes.TestEmailFailed, StatusCodes.Status503ServiceUnavailable);
        options.MapCode(AppSettingErrorCodes.EmailVerificationKeyMissing, StatusCodes.Status409Conflict);
#endif
    }
}
