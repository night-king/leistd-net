#if (LocalIdentity)
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>上下文之间的模型边界：租户注册表不进业务上下文，用户不进控制面上下文。</summary>
/// <remarks>
/// <para>与是否签发 OIDC 令牌无关，所以独立于 <c>AuthenticationModeTests</c>——
/// 后者整体只在签发令牌的形态下存在。</para>
/// <para>把 OIDC 存储拆成独立上下文之后这条断言更该跑：三个上下文（业务、租户控制面、
/// OIDC 存储）各自的模型边界都不能串，否则同库多上下文会争用迁移历史、
/// 或让本该按租户过滤的实体落进不受过滤的上下文。</para>
/// </remarks>
public sealed class ControlPlaneModelSeparationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public void Control_plane_and_tenant_business_models_are_separated()
    {
        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<IdentityControlDbContext>();
        var business = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();

        Assert.NotNull(control.Model.FindEntityType(typeof(TenantRecord)));
        Assert.Null(control.Model.FindEntityType(typeof(User)));
        Assert.Null(business.Model.FindEntityType(typeof(TenantRecord)));
        Assert.NotNull(business.Model.FindEntityType(typeof(User)));
    }

#if (OpenIddictServer)
    /// <summary>OIDC 存储自成一个上下文，不与租户控制面共享模型。</summary>
    /// <remarks>
    /// 两者合并在一个上下文里的代价是：剪掉 OIDC 时要么留 4 张空表、
    /// 要么给迁移与模型快照做条件剪裁。
    /// 若有人把 OpenIddict 实体挪进控制面上下文，这里会红。
    /// </remarks>
    [Fact]
    public void OpenIddict_storage_has_its_own_context()
    {
        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<IdentityControlDbContext>();
        var openIddict = scope.ServiceProvider.GetRequiredService<OpenIddictDbContext>();

        Assert.DoesNotContain(
            control.Model.GetEntityTypes(),
            entityType => entityType.Name.Contains("OpenIddict", StringComparison.Ordinal));
        Assert.Contains(
            openIddict.Model.GetEntityTypes(),
            entityType => entityType.Name.Contains("OpenIddict", StringComparison.Ordinal));
        Assert.Null(openIddict.Model.FindEntityType(typeof(TenantRecord)));
    }
#endif
}
#endif
