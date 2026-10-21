#if (OpenIddictServer)
namespace CompanyName.ProjectName.Application.TenantConnections.Constants;

public static class TenantConnectionScopes
{
    public const string RuntimeRead = "tenant-routing.read";
    public const string MigrationRead = "tenant-migration.read";
}
#endif
