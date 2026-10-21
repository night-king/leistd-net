using Leistd.Data.Querying;
using Microsoft.Extensions.Options;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using Leistd.Ddd.Domain.Repositories;
using System.Security.Claims;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Leistd.Authorization.Subjects;
using Leistd.Timing;

namespace CompanyName.ProjectName.Application.Permissions.Provider;

/// <summary>将模板身份模型适配为权限检查主体。</summary>
public class PermissionSubjectProvider(
    ICurrentUser currentUser,
    IRepository<User, Guid> userRepository,
    IOptions<ClaimTypeOptions> claimTypes,
    IClock clock,
    IQueryableAsyncExecuter asyncExecuter) : IPermissionSubjectProvider
{
    public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default)
        => GetSubjectAsync(currentUser.Id, cancellationToken);

    // 与 ICurrentUser.Id 同一口径：共享规则读出的主体标识是 GUID 才是用户
    public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        => GetSubjectAsync(Guid.TryParse(claimTypes.Value.FindUserId(principal), out var userId) ? userId : null, cancellationToken);

    private async Task<PermissionSubject?> GetSubjectAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (!userId.HasValue)
            return null;

        var userIdValue = userId.Value;
        var user = await userRepository.GetByIdAsync(userIdValue, cancellationToken);

        // 登录时拒绝禁用与锁定账号，但已签发的 Cookie/Bearer 不会因此失效。
        // 主体解析每请求查库，这里是 RBAC 路径上的每请求失效保障——放行就等于"禁用用户"只挡新登录，
        // 已在线的会话照常调用受权限保护的接口，与界面承诺的语义不符。
        //
        // 它不是唯一一道：经应用停用、删除账号时会话与令牌随之撤销，所有端点在认证阶段就拒绝；
        // 这里在 RBAC 路径上兜住绕过应用直接改库的情形，不等会话校验的缓存到期。
        //
        // 检查必须在超管分支之前：被禁用的超管同样要立刻失去权限。
        if (user == null || user.GetAccessStatus(clock.Now) != UserAccessStatus.Allowed)
            return null;

        if (user.IsSuperAdmin)
        {
            return new PermissionSubject(
                userIdValue.ToString(),
                [],
                IsSuperAdmin: true);
        }

        // 只投影角色 Id、不加载成员关系：每个请求都走这里，跟踪下来的成员关系会挡住同一请求里删除该用户
        var userQuery = await userRepository.GetQueryableAsync(cancellationToken);
        var roleIds = (await asyncExecuter.ToListAsync(
                userQuery.Where(u => u.Id == userIdValue).SelectMany(u => u.Roles).Select(ur => ur.RoleId),
                cancellationToken))
            .Select(roleId => roleId.ToString())
            .ToArray();

        return new PermissionSubject(
            userIdValue.ToString(),
            roleIds,
            IsSuperAdmin: false);
    }
}
