#if (LocalIdentity)
using System.Linq.Expressions;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using Leistd.ExceptionHandling;

namespace CompanyName.ProjectName.Application.Users.Validators;

/// <summary>禁止排序表达式访问用户凭据。</summary>
public sealed class UserSortingValidator
{
    public void Validate(IOrderedQueryable<User> query)
    {
        var visitor = new CredentialVisitor();
        // 只检查排序选择器，既有筛选条件不属于调用方提交的排序。
        for (var expression = query.Expression;
             expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable) &&
             call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) or
                 nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending);
             expression = call.Arguments[0])
        {
            visitor.Visit(((UnaryExpression)call.Arguments[1]).Operand);
        }
    }

    private sealed class CredentialVisitor : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.DeclaringType == typeof(TwoFactorCredential) ||
                (node.Member.DeclaringType == typeof(User) &&
                 node.Member.Name is nameof(User.PasswordHash) or nameof(User.SecurityStamp) or nameof(User.TwoFactor)))
            {
                throw new BusinessException(UserErrorCodes.SortingCredentialsForbidden,
                    "Sorting by user credentials is forbidden.");
            }

            return base.VisitMember(node);
        }
    }
}
#endif
