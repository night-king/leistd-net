using System.Reflection;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.MultiTenancy.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 实体模型约定：本项目实体实现 <see cref="IMultiTenant"/>（auth.md），枚举属性按字符串持久化（coding-backend.md §3.8），
/// 只有聚合根拥有仓储（coding-backend.md §6）。
/// </summary>
/// <remarks>
/// 判定在业务上下文的实际模型上做，只检查本项目 Domain 程序集里的实体；组件自带的实体由各组件负责。
/// 漏实现 <see cref="IMultiTenant"/> 的实体不受租户过滤、也不落租户归属，数据会在租户间可见；
/// 枚举按整数存时，调整成员顺序就会把历史数据静默改成另一个值；
/// 子实体一旦声明 DbSet 就会被自动登记仓储，绕过聚合根直接修改。
/// </remarks>
public sealed class EntityModelConventionTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    /// <summary>不实现 <see cref="IMultiTenant"/> 的本项目实体，每项写明理由。当前没有。</summary>
    private static readonly EntityExemption[] Exemptions = [];

    /// <summary>业务上下文里的本项目实体全部满足约定。</summary>
    [Fact]
    public void Project_entities_are_tenant_scoped_and_store_enums_as_strings()
    {
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().Model;
        var domain = typeof(User).Assembly;

        // 防止程序集过滤失效后变成"零个实体、零个问题"的空转通过
        Assert.Contains(model.GetEntityTypes(), entity => entity.ClrType == typeof(User));
        Assert.Empty(FindViolations(model, domain, Exemptions));
    }

    /// <summary>实现 <see cref="IAggregateRoot"/> 的实体有仓储，其余实体没有。</summary>
    [Fact]
    public void Only_aggregate_roots_have_repositories()
    {
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().Model;
        var domain = typeof(User).Assembly;
        var entities = model.GetEntityTypes()
            .Where(e => e.ClrType.Assembly == domain && !e.IsOwned())
            .Select(e => e.ClrType)
            .ToList();

        // 两种实体都在模型里，比对才有正反两面
        Assert.Contains(typeof(User), entities);
        Assert.Contains(typeof(UserRole), entities);
        var mismatched = entities
            .Where(type => typeof(IAggregateRoot).IsAssignableFrom(type)
                != (scope.ServiceProvider.GetService(typeof(IRepository<>).MakeGenericType(type)) is not null))
            .Select(type => type.Name)
            .ToList();
        Assert.Empty(mismatched);
    }

    /// <summary>合规实体通过；缺租户归属、枚举按整数存（含值对象里的）各报一条；白名单内的实体只免租户归属。</summary>
    [Fact]
    public void Synthetic_model_reports_missing_tenant_scope_and_integer_enums()
    {
        var model = SyntheticModel();
        var fixtures = typeof(EntityModelConventionTests).Assembly;

        var violations = FindViolations(model, fixtures, []);

        Assert.Equal(3, violations.Count);
        Assert.Contains(violations, v => v.Contains(nameof(HostOnlyOrder)) && v.Contains(nameof(IMultiTenant)));
        Assert.Contains(violations, v => v.Contains($"{nameof(HostOnlyOrder)}.{nameof(HostOnlyOrder.Status)}") && v.Contains("string"));
        Assert.Contains(violations, v => v.Contains($"{nameof(HostOnlyOrder)}.{nameof(HostOnlyOrder.Shipping)}.{nameof(Shipping.Stage)}"));

        var exempted = FindViolations(model, fixtures, [new(typeof(HostOnlyOrder), "夹具：宿主数据")]);
        Assert.Equal(2, exempted.Count);
        Assert.DoesNotContain(exempted, v => v.Contains(nameof(IMultiTenant)));
    }

    private static List<string> FindViolations(IModel model, Assembly projectAssembly, IReadOnlyList<EntityExemption> exemptions)
    {
        var violations = new List<string>();
        foreach (var entity in model.GetEntityTypes().Where(e => e.ClrType.Assembly == projectAssembly && !e.IsOwned()))
        {
            if (!typeof(IMultiTenant).IsAssignableFrom(entity.ClrType) && exemptions.All(e => e.EntityType != entity.ClrType))
            {
                violations.Add($"{entity.ClrType.Name}: entity must implement {nameof(IMultiTenant)} or be exempted");
            }

            foreach (var (path, property) in ScalarProperties(entity, entity.ClrType.Name))
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                var provider = property.GetProviderClrType() ?? property.GetValueConverter()?.ProviderClrType;
                if (type.IsEnum && provider != typeof(string))
                {
                    violations.Add($"{path}: enum must be stored as string");
                }
            }
        }

        return violations;
    }

    // 值对象（复杂属性）里的列同样受约束，逐层展开
    private static IEnumerable<(string Path, IProperty Property)> ScalarProperties(ITypeBase type, string path)
    {
        foreach (var property in type.GetProperties())
        {
            yield return ($"{path}.{property.Name}", property);
        }

        foreach (var complex in type.GetComplexProperties())
        {
            foreach (var nested in ScalarProperties(complex.ComplexType, $"{path}.{complex.Name}"))
            {
                yield return nested;
            }
        }
    }

    private static IModel SyntheticModel()
    {
        var builder = new ModelBuilder();
        builder.Entity<TenantScopedOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.TenantId);
            entity.Property(order => order.Status).HasConversion<string>();
        });
        builder.Entity<HostOnlyOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Status);
            entity.ComplexProperty(order => order.Shipping, shipping => shipping.Property(s => s.Stage));
        });
        return builder.FinalizeModel();
    }

    private sealed record EntityExemption(Type EntityType, string Reason);

    private enum OrderStatus
    {
        Draft,
        Placed,
    }

    private sealed class TenantScopedOrder : IMultiTenant
    {
        public Guid Id { get; set; }

        public Guid? TenantId { get; set; }

        public OrderStatus? Status { get; set; }
    }

    private sealed class HostOnlyOrder
    {
        public Guid Id { get; set; }

        public OrderStatus Status { get; set; }

        public Shipping Shipping { get; set; } = new();
    }

    private sealed record Shipping
    {
        public OrderStatus Stage { get; set; }
    }
}
