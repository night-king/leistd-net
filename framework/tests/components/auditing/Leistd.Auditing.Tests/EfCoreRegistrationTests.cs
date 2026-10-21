using Leistd.Auditing.Abstractions;
using Leistd.Auditing.EntityFrameworkCore;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Auditing.Tests;

/// <summary>
/// <c>AddAuditingEfCore</c> 的注册面：重复调用不该多出描述符。
/// 审计属性设置器是工厂注册，重复时两条描述符构造等价、危害不大，
/// 但同一条「注册面就是契约」的规则在此家族也要成立，否则口径不一致。
/// </summary>
public class EfCoreRegistrationTests
{
    [Fact]
    public void Repeated_registration_adds_nothing()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddAuditingEfCore());
    }

    // 设置器是替换口：宿主先注册的实现（如按自有身份模型填审计人）不被组件默认值盖掉，DDD 基座重复调用也一样
    [Fact]
    public void Host_registered_property_setter_is_kept()
    {
        var services = new ServiceCollection();
        services.AddTransient<IAuditPropertySetter, HostSetter>();

        services.AddAuditingEfCore();
        services.AddAuditingEfCore();

        services.AssertImplementedBy<IAuditPropertySetter, HostSetter>();
    }

    private sealed class HostSetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object entityEntry) { }

        public void SetModificationProperties(object entityEntry) { }

        public void SetDeletionProperties(object entityEntry) { }
    }
}
