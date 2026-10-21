#if (LocalIdentity)
using System.Security.Claims;
using System.Text.Json;
using Leistd.Authorization.Subjects;
using Leistd.Security.Claims;
using Leistd.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>权限主体的两个解析入口同一口径：按当前主体与按显式主体解析，得到同一个权限主体。</summary>
/// <remarks>
/// 策略管道为被授权的主体判权时走显式入口；两边口径一旦分叉，同一个人在不同入口下权限不同。
/// </remarks>
public sealed class PermissionSubjectResolutionTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task The_explicit_entry_resolves_the_same_subject_as_the_current_one()
    {
        var adminId = await ReadAdminIdAsync();
        var principal = Principal(adminId.ToString());

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var provider = services.GetRequiredService<IPermissionSubjectProvider>();
        using var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin();

        PermissionSubject? current;
        using (services.GetRequiredService<ICurrentPrincipalAccessor>().Change(principal))
        {
            current = await provider.GetCurrentSubjectAsync();
        }

        var explicitSubject = await provider.GetSubjectAsync(principal);

        Assert.NotNull(current);
        Assert.NotNull(explicitSubject);
        Assert.Equal(current.UserId, explicitSubject.UserId);
        Assert.Equal(current.IsSuperAdmin, explicitSubject.IsSuperAdmin);
        Assert.Equal(current.RoleIds.Order(), explicitSubject.RoleIds.Order());
        Assert.True(explicitSubject.IsSuperAdmin);
    }

    // 机器主体的标识不是 GUID：不是用户，解析不出权限主体
    [Fact]
    public async Task A_machine_principal_resolves_to_no_subject()
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IPermissionSubjectProvider>();

        Assert.Null(await provider.GetSubjectAsync(Principal(ClientSubject.Format("worker-1"))));
    }

    private async Task<Guid> ReadAdminIdAsync()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var me = JsonDocument.Parse(await admin.Client.GetStringAsync("/api/v1/auth/me"));
        return me.RootElement.GetProperty("id").GetGuid();
    }

    private static ClaimsPrincipal Principal(string subject)
        => new(new ClaimsIdentity([new Claim(CustomClaimTypes.Subject, subject)], "Test"));
}
#endif
