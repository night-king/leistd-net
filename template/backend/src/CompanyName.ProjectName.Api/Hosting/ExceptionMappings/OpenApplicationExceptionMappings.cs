#if (OpenIddictServer)
using CompanyName.ProjectName.Application.OpenApplications.Errors;
using Leistd.ExceptionHandling.Options;

namespace CompanyName.ProjectName.Api.Hosting.ExceptionMappings;

internal static class OpenApplicationExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        options.MapCode(OpenAppErrorCodes.NotFound, StatusCodes.Status404NotFound);
        ApiExceptionMappings.Map(options, StatusCodes.Status409Conflict,
            OpenAppErrorCodes.ClientIdTaken,
            OpenAppErrorCodes.SecretResetConfidentialOnly);
    }
}
#endif
