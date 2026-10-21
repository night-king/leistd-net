using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.ExceptionHandling.Options;

namespace CompanyName.ProjectName.Api.Hosting.ExceptionMappings;

internal static class UserExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        ApiExceptionMappings.Map(options, StatusCodes.Status403Forbidden,
#if (RemoteTokenAuth)
            UserErrorCodes.LocalAccessDisabled,
            UserErrorCodes.LocalMemberMissing,
#endif
            UserErrorCodes.ManageRolesRequired,
#if (LocalIdentity)
            UserErrorCodes.SuperAdminDeleteForbidden,
#endif
            UserErrorCodes.SuperAdminDisableForbidden,
            UserErrorCodes.SuperAdminDisableSelfForbidden,
            UserErrorCodes.SuperAdminOperationForbidden,
#if (LocalIdentity)
            UserErrorCodes.SuperAdminResetPasswordForbidden,
#endif
            UserErrorCodes.SuperAdminUpdateForbidden);
        options.MapCode(UserErrorCodes.NotFound, StatusCodes.Status404NotFound);
        ApiExceptionMappings.Map(options, StatusCodes.Status409Conflict,
            UserErrorCodes.EmailTaken,
            UserErrorCodes.UsernameTaken);
    }
}
