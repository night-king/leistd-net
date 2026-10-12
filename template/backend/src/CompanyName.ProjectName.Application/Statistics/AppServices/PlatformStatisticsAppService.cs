using CompanyName.ProjectName.Application.Statistics.Dtos;
#if (LocalIdentity && IncludeOperationRecords)
using CompanyName.ProjectName.Application.OperationRecords.Provider;
#endif
#if (LocalIdentity && (IncludeOperationRecords || IncludeMultiTenancy))
using CompanyName.ProjectName.Domain.Statistics.Abstractions;
#endif
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
#endif
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Data.Querying;
using Leistd.Ddd.Application.AppServices;
#if (LocalIdentity && IncludeMultiTenancy)
using Leistd.MultiTenancy.Context;
#endif
using Leistd.Timing;

namespace CompanyName.ProjectName.Application.Statistics.AppServices;

/// <summary>平台统计应用服务。</summary>
/// <remarks>
/// 只读汇总，各项各查一次，不开工作单元：数字之间不要求同一快照。
/// 区间按 UTC 日划分，与展示时区无关：本区间是含今天在内往前 <c>Days</c> 个整日，上一区间是紧邻其前的等长区间。
/// </remarks>
public class PlatformStatisticsAppService(
    IUserRepository userRepository,
#if (LocalIdentity)
    IRoleRepository roleRepository,
#endif
#if (LocalIdentity && (IncludeOperationRecords || IncludeMultiTenancy))
    IPlatformStatisticsStore statisticsStore,
#endif
#if (LocalIdentity && IncludeMultiTenancy)
    ICurrentTenant currentTenant,
#endif
    IClock clock,
    IQueryableAsyncExecuter asyncExecuter) : BaseAppService, IPlatformStatisticsAppService
{
    /// <inheritdoc />
    public async Task<PlatformStatisticsOutputDto> GetAsync(
        GetPlatformStatisticsInputDto input,
        CancellationToken cancellationToken = default)
    {
        var now = clock.Now;
        var currentEnd = now.Date.AddDays(1);
        var currentStart = currentEnd.AddDays(-input.Days);
        var previousStart = currentStart.AddDays(-input.Days);

        // 用户数按创建时间回推：上一区间结束时的总数即本区间开始前已建的用户，已删除的用户不计入任何一项
        var users = await userRepository.GetQueryableAsync(cancellationToken);
        var totalUsers = await asyncExecuter.CountAsync(users, cancellationToken);
        var usersBeforeCurrent = await asyncExecuter.CountAsync(
            users.Where(user => user.CreationTime < currentStart), cancellationToken);
        var usersBeforePrevious = await asyncExecuter.CountAsync(
            users.Where(user => user.CreationTime < previousStart), cancellationToken);
#if (LocalIdentity && IncludeOperationRecords)

        // 登录趋势与失败数取自登录的操作记录；失败的上一区间值与本区间同一次查询取回
        var succeededByDay = await statisticsStore.CountOperationRecordsByDayAsync(
            OperationRecordActions.AuthLoginSucceeded, currentStart, currentEnd, cancellationToken);
        var failedByDay = await statisticsStore.CountOperationRecordsByDayAsync(
            OperationRecordActions.AuthLoginFailed, previousStart, currentEnd, cancellationToken);
        var loginTrend = Enumerable.Range(0, input.Days)
            .Select(offset => DateOnly.FromDateTime(currentStart.AddDays(offset)))
            .Select(date => new LoginTrendPointOutputDto
            {
                Date = date,
                Succeeded = succeededByDay.GetValueOrDefault(date),
                Failed = failedByDay.GetValueOrDefault(date)
            })
            .ToList();
        var firstCurrentDate = DateOnly.FromDateTime(currentStart);

        // 登录成功记录的目标就是登录的用户
        var activeUsers = new StatisticValueOutputDto
        {
            Value = await statisticsStore.CountOperationRecordTargetsAsync(
                OperationRecordActions.AuthLoginSucceeded, currentStart, currentEnd, cancellationToken),
            Previous = await statisticsStore.CountOperationRecordTargetsAsync(
                OperationRecordActions.AuthLoginSucceeded, previousStart, currentStart, cancellationToken)
        };
#elif (LocalIdentity)

        // 没有登录记录可查，退回用户行上的最近登录时间：它只保留最后一次，答不出上一区间有多少人登录过
        var activeUsers = new StatisticValueOutputDto
        {
            Value = await asyncExecuter.CountAsync(
                users.Where(user => user.LastLogin != null && user.LastLogin.Time >= currentStart), cancellationToken)
        };
#endif

        return new PlatformStatisticsOutputDto
        {
            Days = input.Days,
            TotalUsers = new StatisticValueOutputDto { Value = totalUsers, Previous = usersBeforeCurrent },
            NewUsers = new StatisticValueOutputDto
            {
                Value = totalUsers - usersBeforeCurrent,
                Previous = usersBeforeCurrent - usersBeforePrevious
            },
#if (LocalIdentity)
            ActiveUsers = activeUsers,
#endif
#if (LocalIdentity && IncludeOperationRecords)
            FailedLogins = new StatisticValueOutputDto
            {
                Value = loginTrend.Sum(day => day.Failed),
                Previous = failedByDay.Where(day => day.Key < firstCurrentDate).Sum(day => day.Value)
            },
            LoginTrend = loginTrend,
#endif
#if (LocalIdentity)
            Attention = await GetAttentionAsync(users, now, cancellationToken)
#else
            // 资源服务形态的用户行由令牌投影而来，锁定、两步验证与邮箱验证都归签发方，本服务没有可关注的状态
            Attention = new PlatformAttentionOutputDto()
#endif
        };
    }

#if (LocalIdentity)
    private async Task<PlatformAttentionOutputDto> GetAttentionAsync(
        IQueryable<User> users,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // 管理员按内置管理员角色的成员关系判定；成员关系与角色各自带租户与软删除过滤
        var adminRoleIds = (await roleRepository.GetQueryableAsync(cancellationToken))
            .Where(role => role.Name == AdminConstant.RoleName)
            .Select(role => role.Id);

        return new PlatformAttentionOutputDto
        {
            LockedUsers = await asyncExecuter.CountAsync(users.Where(User.LockedOutAt(now)), cancellationToken),
            AdminsWithoutTwoFactor = await asyncExecuter.CountAsync(
                users.Where(user => user.TwoFactor == null
                    && user.Roles.Any(membership => adminRoleIds.Contains(membership.RoleId))),
                cancellationToken),
            UnconfirmedEmailUsers = await asyncExecuter.CountAsync(
                users.Where(user => !user.EmailConfirmed), cancellationToken),
#if (IncludeMultiTenancy)
            // 租户注册表是宿主的数据，租户管理员不该看到别的租户的状态
            DisabledTenants = currentTenant.IsAvailable
                ? null
                : await statisticsStore.CountInactiveTenantsAsync(cancellationToken),
#endif
        };
    }
#endif
}
