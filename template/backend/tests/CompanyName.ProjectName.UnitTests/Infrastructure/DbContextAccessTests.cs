using System.Reflection;
using CompanyName.ProjectName.Api.Controllers;
using CompanyName.ProjectName.Infrastructure.Persistence;
// 各层都有名为 DependencyInjection 的类，用别名指明取 Application 层的
using ApplicationDependencyInjection = CompanyName.ProjectName.Application.DependencyInjection;

namespace CompanyName.ProjectName.UnitTests.Infrastructure;

/// <summary>业务 DbContext 只能经 <c>IDbContextProvider</c> 取得，不得直接注入。</summary>
/// <remarks>
/// <para>直接注入的 <see cref="MyProjectDbContext"/> 在作用域创建时就绑定了连接，
/// 不跟随租户路由也不参与工作单元：独立库租户的请求或切过租户的后台作业拿到它，
/// 读写的是宿主库——不报错，数据悄悄落错库或被漏掉。经提供器取得的上下文按当前租户解析连接，
/// 并由工作单元校验连接归属。</para>
/// <para>控制面上下文固定连宿主库、不参与租户路由，不受此限。</para>
/// </remarks>
public sealed class DbContextAccessTests
{
    [Fact]
    public void No_service_takes_the_business_DbContext_in_its_constructor()
    {
        Assembly[] assemblies =
        [
            // 单元测试同时引用 Api 与 DbMigrator，两者都有顶级 Program，这里用 Api 自己的类型定位程序集
            typeof(BaseController).Assembly,
            typeof(MyProjectDbContext).Assembly,
            typeof(ApplicationDependencyInjection).Assembly
        ];

        var offenders = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(constructor => constructor.GetParameters()
                    .Any(parameter => typeof(MyProjectDbContext).IsAssignableFrom(parameter.ParameterType))))
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(offenders);
    }
}
