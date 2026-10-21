using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Leistd.Data.EntityFrameworkCore.Modeling;

/// <summary>基于原生 EF 元数据配置命名查询过滤器。</summary>
public static class ModelBuilderExtensions
{
    /// <summary>为实现契约的非 owned 根实体应用命名过滤器；派生类型沿用根声明。</summary>
    /// <remarks>不同名称按 AND 组合，同名后写覆盖；可经原生 IgnoreQueryFilters 按名忽略。支持 shared-type 实体。</remarks>
    public static void ApplyGlobalFilters<TInterface>(this ModelBuilder modelBuilder,
        string filterName, Expression<Func<TInterface, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(filterName);
        ArgumentNullException.ThrowIfNull(expression);
        var entities = modelBuilder.Model.GetEntityTypes()
            .Where(entity => entity.BaseType is null && !entity.IsOwned() && typeof(TInterface).IsAssignableFrom(entity.ClrType));
        foreach (var entity in entities)
        {
            var parameter = Expression.Parameter(entity.ClrType);
            var body = ReplacingExpressionVisitor.Replace(expression.Parameters.Single(), parameter, expression.Body);
            entity.SetQueryFilter(filterName, Expression.Lambda(body, parameter));
        }
    }
}
