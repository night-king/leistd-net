#if (LocalIdentity)
using CompanyName.ProjectName.Application.Tenants.Errors;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Application.Auth.SignIn;
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.Constants;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Tenants.Dtos;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Application.AppServices;
using Leistd.Ddd.Domain.Repositories;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Errors;
using Leistd.MultiTenancy.Stores;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Leistd.UnitOfWork;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.Application.Tenants.AppServices;

/// <inheritdoc cref="ITenantImpersonationAppService" />
/// <remarks>
/// <c>internal sealed</c> 而非 <c>public</c>：它依赖 <c>SessionIssuer</c>（internal），
/// 公开类的构造参数可访问性不能更低（CS0051）。与 <c>AuthAppService</c> 同型——
/// 对外契约是接口，实现不必公开。
/// </remarks>
internal sealed class TenantImpersonationAppService(
    ITenantStore tenantStore,
    IRepository<User, Guid> userRepository,
    SessionIssuer sessionIssuer,
    IUserSessionAppService userSessionAppService,
    ICurrentUser currentUser,
    ICurrentPrincipalAccessor currentPrincipalAccessor,
    ICurrentTenant currentTenant,
    IOperationRecorder operationRecorder,
    IUnitOfWorkManager unitOfWorkManager,
    ILogger<TenantImpersonationAppService> logger) : BaseAppService, ITenantImpersonationAppService
{
    /// <inheritdoc />
    public async Task<ClaimsPrincipal> ImpersonateAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // 不重复"只有宿主能发起"：App.Tenants.Impersonation 是 Host 侧别，
        // DefaultPermissionChecker 按当前侧别判定且与是否授予无关，租户上下文一律不通过。
        // 在这里再拦一次只是把同一个不变量写两遍。
        //
        // 下面这条则是承重的：没有任何权限机制表达"不能嵌套模拟"。
        // 嵌套后"结束模拟"只能回退一层，顶栏模拟提示里的发起人会与实际回退目标不符。
        if (currentUser.FindClaim(ImpersonationClaimTypes.ImpersonatorUserId) is not null)
        {
            throw new BusinessException(TenantErrorCodes.AlreadyImpersonating, "Already impersonating; end the current impersonation first.");
        }

        // 端点要求 App.Tenants.Impersonation 权限，未认证的请求在授权阶段已被挡下；
        // 这里取不到用户说明调用方绕过了端点授权，是编程错误。
        var impersonatorId = currentUser.Id
            ?? throw new InvalidOperationException("Impersonation requires an authenticated caller; the endpoint authorization was bypassed.");

        var tenant = await tenantStore.FindAsync(tenantId, cancellationToken)
                     ?? throw new BusinessException(MultiTenancyErrorCodes.NotFound, $"Tenant '{tenantId}' not found.");

        if (!tenant.IsActive)
        {
            throw new BusinessException(MultiTenancyErrorCodes.NotActive, $"Tenant '{tenant.Name}' is deactivated.");
        }

        ClaimsPrincipal principal;

        // 进入目标租户上下文后，仓储查询自动按 TenantId 分区——按用户名找到的
        // 必然是该租户的管理员，不会串到宿主或别的租户。
        //
        // 整个块包进租户上下文内新开的工作单元：查管理员、SignInAsync 里的写入、以及下面那条审计记录，
        // 都必须落进这个租户自己的库并同生共死。只切 Change 不够——本方法没有环境工作单元，仓储取到的是
        // 请求作用域里早已绑定宿主库的 DbContext；分库租户的管理员在它自己的库里，于是报"没有可模拟的 admin"。
        using (currentTenant.Change(tenant.Id, tenant.Name))
        using (var tenantUnitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            var admin = await userRepository.GetFirstAsync(
                u => u.Username == AdminConstant.TenantAdminUsername,
                q => q.OrderBy(u => u.Id),
                cancellationToken)
                ?? throw new BusinessException(TenantErrorCodes.AdministratorNotFound,
                    $"Tenant '{tenant.Name}' has no '{AdminConstant.TenantAdminUsername}' user to impersonate.");

            var claims = new List<Claim>
            {
                new(ImpersonationClaimTypes.ImpersonatorUserId, impersonatorId.ToString())
            };

            // 显示名快照与操作人列同一取法（Name ?? Username）：模拟期间的记录写"由谁模拟操作"，
            // 而此后会话主体是被模拟者，发起人是谁只剩这条声明记得。
            if ((currentUser.Name ?? currentUser.Username) is { } impersonatorName)
            {
                claims.Add(new Claim(ImpersonationClaimTypes.ImpersonatorName, impersonatorName));
            }

            // 发起人在宿主时不写租户声明——缺席即宿主，与 tenant_id 同一口径。
            if (currentUser.TenantId is { } impersonatorTenantId)
            {
                claims.Add(new Claim(ImpersonationClaimTypes.ImpersonatorTenantId, impersonatorTenantId.ToString()));
            }

            // 模拟不是一次认证：模拟会话沿用发起人的认证时刻，退出时再原样带回（见 SessionIssuer.SignInAsync）
            principal = await sessionIssuer.SignInAsync(
                admin, roleNames: null, additionalClaims: claims, cancellationToken: cancellationToken,
                authenticatedAt: CurrentAuthenticationTime());

            logger.LogWarning(
                "Impersonation started: user {ImpersonatorId} is now acting as {Username} in tenant {TenantName} ({TenantId})",
                impersonatorId, admin.Username, tenant.Name, tenant.Id);

            // **记录必须写在这个租户上下文块内部**：这样 TenantId 落成被模拟的那个租户，
            // 记录才对该租户可见。这是一条信任属性，不是实现细节——一个能让宿主悄悄进来
            // 而租户看不见的审计系统，对租户没有价值。租户管理员否则会看到"自己的用户"
            // 做了他没做过的事，却查不到是谁以他的名义进来的。
            //
            // 操作人此刻仍是发起模拟的宿主管理员（本次请求的主体未变），正是要留的那个人。
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.ImpersonationStarted,
                OperationTarget.For(admin.Id, admin.DisplayName ?? admin.Username),
                PermissionConstant.Tenants.Impersonation,
                cancellationToken);

            await tenantUnitOfWork.CompleteAsync(cancellationToken);
        }

        // 宿主侧再留一条：上面那条落在租户的库里，宿主的操作记录里看不到"我们的人进了哪家"。
        // 两层各一条而不是一条跨层可见：一个工作单元只连一个库，而分库租户的记录本就不在宿主库里。
        //
        // 放在租户那一步提交之后：租户侧失败（没有 admin、库连不上）时宿主侧不留"已进入"的假记录。
        // 反过来这一步失败时请求整体报错、不下发 Cookie，租户侧多出的那条仍是一次真实的尝试。
        using (var impersonatorUnitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.TenantImpersonationStarted,
                OperationTarget.For(tenant.Id, tenant.DisplayName ?? tenant.Name),
                PermissionConstant.Tenants.Impersonation,
                cancellationToken);
            await impersonatorUnitOfWork.CompleteAsync(cancellationToken);
        }

        // Cookie 即将整体换成被模拟者的，发起人原来那个会话随之结束（退出模拟时另登记一个新会话）。
        // 放在最后：前面任何一步失败都不下发新 Cookie，这时原会话必须还在，否则发起人会被连带踢下线。
        await userSessionAppService.EndCurrentSessionAsync(cancellationToken);

        return principal;
    }

    /// <inheritdoc />
    public async Task<ClaimsPrincipal> EndImpersonationAsync(CancellationToken cancellationToken = default)
    {
        var impersonatorId = ReadImpersonatorId()
            ?? throw new BusinessException(TenantErrorCodes.NotImpersonating, "The current session is not impersonating.");

        var impersonatorTenantId = ReadImpersonatorTenantId();

        // 先把被模拟的一方记下来：切回发起人上下文之后，当前租户与会话主体都要换掉。
        var impersonatedTenantId = currentTenant.Id;
        var impersonatedTenantName = currentTenant.Name;
        var impersonatedUser = OperationTarget.For(currentUser.Id?.ToString(), currentUser.Name ?? currentUser.Username);

        // 宿主侧记录的目标名要与开始时那条一致（显示名优先），得回注册表取。
        // 注册表在控制库的上下文里，和下面查发起人用的不是同一个 DbContext 类型：放进同一个工作单元
        // 就要两个上下文共享一个事务。所以与开始模拟时同一条路——宿主上下文、工作单元之外单独读。
        TenantConfiguration? impersonatedTenant = null;
        if (impersonatedTenantId is { } impersonatedId)
        {
            using (currentTenant.Change(null))
            {
                impersonatedTenant = await tenantStore.FindAsync(impersonatedId, cancellationToken);
            }
        }

        ClaimsPrincipal principal;

        // 回到发起人所在的上下文再查人：发起人是宿主时为 null，正好落回宿主行。
        //
        // 同样必须新开工作单元，而且这里比开始模拟更容易出错：此刻会话本身就是被模拟的租户身份，
        // 请求作用域里的 DbContext 绑的是租户的库，切回宿主后若复用它，就会在租户库里找宿主管理员，
        // 得出"发起模拟的用户已不存在"——分库租户进得去、出不来。
        using (currentTenant.Change(impersonatorTenantId))
        using (var impersonatorUnitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            var impersonator = await userRepository.GetFirstAsync(
                u => u.Id == impersonatorId,
                q => q.OrderBy(u => u.Id),
                cancellationToken)
                ?? throw new BusinessException(UserErrorCodes.NotFound, "The impersonating user no longer exists.");

            // 换回发起人也不是一次认证：沿用模拟会话里带着的、发起人原来的认证时刻
            principal = await sessionIssuer.SignInAsync(
                impersonator, cancellationToken: cancellationToken, authenticatedAt: CurrentAuthenticationTime());

            logger.LogWarning(
                "Impersonation ended: session returned to user {ImpersonatorId}", impersonatorId);

            // 发起人一侧：目标是刚离开的租户。操作人必须换成发起人——SignInAsync 只往响应里种 Cookie，
            // 本次请求的主体仍是被模拟的租户管理员；不换的话这条宿主记录的操作人 Id 会是租户里的账号，
            // 而两边管理员都叫 admin，名字上看不出错。
            using (currentPrincipalAccessor.Change(principal))
            {
                await operationRecorder.RecordSucceededAsync(
                    OperationRecordActions.TenantImpersonationEnded,
                    OperationTarget.For(
                        impersonatedTenantId?.ToString(),
                        impersonatedTenant?.DisplayName ?? impersonatedTenant?.Name ?? impersonatedTenantName),
                    PermissionConstant.Tenants.Impersonation,
                    cancellationToken);
            }

            await impersonatorUnitOfWork.CompleteAsync(cancellationToken);
        }

        // 被模拟的租户一侧：让租户看得到那次进来的人什么时候走的。此刻仍在租户上下文、仍是模拟态主体，
        // 所以操作人是被模拟者、模拟者名来自声明，与模拟期间的其他记录同一口径。
        // 放在发起人一侧之后：发起人已不存在等失败会让模拟态保持原样，这时不该留"已结束"。
        using (var impersonatedUnitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.ImpersonationEnded,
                impersonatedUser,
                PermissionConstant.Tenants.Impersonation,
                cancellationToken);
            await impersonatedUnitOfWork.CompleteAsync(cancellationToken);
        }

        // 模拟会话登记在被模拟的租户里，此刻环境租户仍是它，正好就地结束
        await userSessionAppService.EndCurrentSessionAsync(cancellationToken);

        return principal;
    }

    /// <inheritdoc />
    public async Task<ImpersonationStatusOutputDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (ReadImpersonatorId() is not { } impersonatorId)
        {
            return new ImpersonationStatusOutputDto { IsImpersonating = false };
        }

        // 租户显示名要回注册表取：当前租户上下文只带着租户名（acme），而操作记录写的是显示名。
        // 注册表在控制库的上下文里，与下面查发起人不是同一个 DbContext 类型，所以放在工作单元之外单独读。
        TenantConfiguration? tenant = null;
        if (currentTenant.Id is { } tenantId)
        {
            using (currentTenant.Change(null))
            {
                tenant = await tenantStore.FindAsync(tenantId, cancellationToken);
            }
        }

        string? impersonatorName;
        // 与结束模拟同理：会话是租户身份，回到发起人上下文查人必须新开工作单元。
        using (currentTenant.Change(ReadImpersonatorTenantId()))
        using (var impersonatorUnitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            var impersonator = await userRepository.GetFirstAsync(
                u => u.Id == impersonatorId, q => q.OrderBy(u => u.Id), cancellationToken);
            impersonatorName = impersonator?.DisplayName ?? impersonator?.Username;
            await impersonatorUnitOfWork.CompleteAsync(cancellationToken);
        }

        return new ImpersonationStatusOutputDto
        {
            IsImpersonating = true,
            ImpersonatorName = impersonatorName,
            TenantName = tenant?.DisplayName ?? tenant?.Name ?? currentTenant.Name
        };
    }

    private Guid? ReadImpersonatorId() =>
        Guid.TryParse(currentUser.FindClaim(ImpersonationClaimTypes.ImpersonatorUserId)?.Value, out var id)
            ? id
            : null;

    private Guid? ReadImpersonatorTenantId() =>
        Guid.TryParse(currentUser.FindClaim(ImpersonationClaimTypes.ImpersonatorTenantId)?.Value, out var id)
            ? id
            : null;

    /// <summary>当前会话的实际认证时刻；读不出时按"很久以前"处理，依赖它的判据随之要求重新认证。</summary>
    private DateTimeOffset CurrentAuthenticationTime() =>
        SessionIssuer.ReadAuthenticationTime(currentUser.FindClaim(SessionIssuer.AuthenticationTimeClaimType))
        ?? DateTimeOffset.UnixEpoch;
}
#endif
