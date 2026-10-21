using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.ExceptionHandling.Options;

namespace CompanyName.ProjectName.Api.Hosting.ExceptionMappings;

internal static class RoleExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        options.MapCode(RoleErrorCodes.NotFound, StatusCodes.Status404NotFound);
        ApiExceptionMappings.Map(options, StatusCodes.Status409Conflict,
            RoleErrorCodes.NameAlreadyUsed,
            RoleErrorCodes.RoleStillAssigned,
            RoleErrorCodes.StaticRoleCannotBeDeleted);
    }
}
