using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.DataScope.Abstractions;

namespace Leistd.Authorization.DataScope.Services;

/// <summary>默认数据范围应用器。</summary>
/// <remarks>
/// 被分配的多个范围之间取并集；没有任何分配或分配的范围没有对应 Provider 时按拒绝处理。
/// 租户隔离与软删除等全局查询过滤器始终与业务范围取交集。
/// </remarks>
public class DefaultDataScopeApplier(
    IPermissionSubjectProvider subjectProvider,
    IServiceProvider serviceProvider,
    IDataScopeAssignmentProvider assignmentProvider) : IDataScopeApplier
{
    /// <inheritdoc />
    public async ValueTask<IQueryable<TEntity>> ApplyAsync<TEntity>(
        IQueryable<TEntity> query,
        string resourceName,
        string operation,
        CancellationToken cancellationToken = default)
    {
        var subject = await subjectProvider.GetCurrentSubjectAsync(cancellationToken);
        if (subject == null)
            return query.Where(_ => false);

        if (subject.IsSuperAdmin)
            return query;

        var assignments = await assignmentProvider.GetAssignmentsAsync(
            subject,
            resourceName,
            operation,
            cancellationToken);

        // 再按当前资源与操作过滤一遍，不采信分配来源漏写条件返回的其他资源或操作的分配
        assignments = [.. assignments.Where(x =>
            string.Equals(x.ResourceName, resourceName, StringComparison.Ordinal) &&
            string.Equals(x.Operation, operation, StringComparison.Ordinal))];

        if (assignments.Count == 0)
            return query.Where(_ => false);

        var context = new DataScopeContext(subject, resourceName, operation, assignments);

        var providers = serviceProvider
            .GetServices<IDataScopeProvider<TEntity>>()
            .Where(x => x.ResourceName == resourceName)
            .ToDictionary(x => x.ScopeName, StringComparer.Ordinal);

        Expression<Func<TEntity, bool>>? combined = null;

        foreach (var assignment in assignments)
        {
            if (!providers.TryGetValue(assignment.ScopeName, out var provider))
            {
                // 分配了一个没有对应 Provider 的范围：无法翻译成谓词，按默认拒绝跳过。
                continue;
            }

            // 谓词非空：_ => false 并入并集即无贡献，“全部可见”须显式返回 _ => true
            var predicate = await provider.BuildPredicateAsync(context, cancellationToken);

            combined = combined == null ? predicate : Or(combined, predicate);
        }

        return combined == null ? query.Where(_ => false) : query.Where(combined);
    }

    private static Expression<Func<TEntity, bool>> Or<TEntity>(
        Expression<Func<TEntity, bool>> left,
        Expression<Func<TEntity, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "x");
        var body = Expression.OrElse(
            new ParameterRebinder(left.Parameters[0], parameter).Visit(left.Body),
            new ParameterRebinder(right.Parameters[0], parameter).Visit(right.Body));

        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    // 把两个独立 Lambda 的参数统一到同一个参数上，否则合并后的表达式无法被翻译。
    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
