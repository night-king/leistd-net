using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using CompanyName.ProjectName.Application.Settings.Timing;
using CompanyName.ProjectName.Application.Statistics.Dtos;
#if (LocalIdentity && IncludeOperationRecords)
using CompanyName.ProjectName.Application.OperationRecords.Provider;
#endif
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Constants;
#endif
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Auditing.Abstractions;
using Leistd.Data.Filters;
using Leistd.Data.Querying;
using Leistd.Ddd.Application.AppServices;
#if (LocalIdentity && IncludeMultiTenancy)
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.Management.Dtos;
#endif
#if (LocalIdentity && IncludeOperationRecords)
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Stores;
#endif
using Leistd.Timing;

namespace CompanyName.ProjectName.Application.Statistics.AppServices;

/// <summary>平台统计应用服务。</summary>
/// <remarks>
/// <para>只读汇总，各项各查一次，不开工作单元：数字之间不要求同一快照。</para>
/// <para>区间按请求的时区解释：起止日换算成 UTC，统计桶也按同一时区切，夏令时当天的日桶是 23 或 25 小时；
/// 时钟只读一次，区间含今天时截到此刻。上一区间按日历前移同样的天数，比较相同的本地进度。</para>
/// <para>数据归哪个组件就经哪个组件的契约取：登录记录（含已归档的）经操作记录的读取契约，租户状态经租户管理用例。</para>
/// </remarks>
public class PlatformStatisticsAppService(
    IUserRepository userRepository,
#if (LocalIdentity)
    IRoleRepository roleRepository,
#endif
#if (LocalIdentity && IncludeOperationRecords)
    IOperationRecordReader operationRecordReader,
#endif
#if (LocalIdentity && IncludeMultiTenancy)
    ITenantManagementService tenantManagementService,
    ICurrentTenant currentTenant,
#endif
    IUserTimeZoneProvider userTimeZoneProvider,
    IDataFilter dataFilter,
    IClock clock,
    IQueryableAsyncExecuter asyncExecuter) : BaseAppService, IPlatformStatisticsAppService
{
    /// <inheritdoc />
    public async Task<PlatformStatisticsOutputDto> GetAsync(
        GetPlatformStatisticsInputDto input,
        CancellationToken cancellationToken = default)
    {
        var now = clock.Now;
        var period = ResolvePeriod(input, now);

#if (LocalIdentity)
        var users = await userRepository.GetQueryableAsync(cancellationToken);

#endif
        // 过去某一时刻的用户数与区间内的注册数要算上之后被删除的用户：关掉软删除过滤、按创建与删除时间回推。
        // 租户过滤保持开启；过滤器在执行时读取开关，查询须在作用域内执行
        StatisticValueOutputDto totalUsers, newUsers;
        using (dataFilter.Disable<ISoftDelete>())
        {
            var everCreated = await userRepository.GetQueryableAsync(cancellationToken);
            var created = await CountByBucketsAsync(everCreated, user => user.CreationTime, period.Edges, cancellationToken);
            var deleted = await CountByBucketsAsync(
                everCreated.Where(user => user.IsDeleted && user.DeletionTime != null),
                user => user.DeletionTime!.Value,
                period.Edges,
                cancellationToken);

            // 各桶末的存量由起点的存量逐桶加上注册、减去删除推出来，最后一项就是期末存量
            var stock = await CountExistingAsync(everCreated, period.Start, cancellationToken);
            var stockSeries = new int[created.Count];
            for (var i = 0; i < stockSeries.Length; i++)
            {
                stock += created[i] - deleted[i];
                stockSeries[i] = stock;
            }

            totalUsers = new StatisticValueOutputDto
            {
                Value = stock,
                Previous = await CountExistingAsync(everCreated, period.PreviousEnd, cancellationToken),
                Series = stockSeries
            };
            newUsers = new StatisticValueOutputDto
            {
                Value = created.Sum(),
                Previous = await CountCreatedAsync(everCreated, period.PreviousStart, period.PreviousEnd, cancellationToken),
                Series = created
            };
        }

#if (LocalIdentity && IncludeOperationRecords)
        // 统计只出聚合数、不下发单条记录，可见性不收窄：登录成功记录对租户读者只显示本人的，按它数活跃用户只会数出自己
        var succeeded = await CountLoginsAsync(OperationRecordActions.AuthLoginSucceeded, period, distinctTargets: false, cancellationToken);
        var failedAlerts = await CountLoginsAsync(OperationRecordActions.AuthLoginFailed, period, distinctTargets: false, cancellationToken);
        // 登录成功记录的目标就是登录的用户
        var activeUsers = await CountLoginsAsync(OperationRecordActions.AuthLoginSucceeded, period, distinctTargets: true, cancellationToken);
#elif (LocalIdentity)
        // 没有登录记录可查，退回用户行上的最近登录时间：它只保留最后一次，答不出上一区间与各桶里有多少人登录过
        var activeStart = period.Start;
        var activeEnd = period.End;
        var activeUsers = new StatisticValueOutputDto
        {
            Value = await asyncExecuter.CountAsync(
                users.Where(user => user.LastLogin != null
                    && user.LastLogin.Time >= activeStart
                    && user.LastLogin.Time < activeEnd),
                cancellationToken)
        };
#endif

        return new PlatformStatisticsOutputDto
        {
            Range = new StatisticsRangeOutputDto
            {
                StartDate = period.StartDate,
                EndDate = period.EndDate,
                TimeZone = period.TimeZone.Id,
                Granularity = period.Granularity,
                Start = period.Start,
                End = period.End,
                PreviousStart = period.PreviousStart,
                PreviousEnd = period.PreviousEnd
            },
            Buckets = period.Edges.Length == 0 ? [] : period.Edges[..^1],
            TotalUsers = totalUsers,
            NewUsers = newUsers,
#if (LocalIdentity)
            ActiveUsers = activeUsers,
#endif
#if (LocalIdentity && IncludeOperationRecords)
            SucceededLogins = succeeded,
            FailedLoginAlerts = failedAlerts,
#endif
#if (LocalIdentity)
            Attention = await GetAttentionAsync(users, now, cancellationToken)
#else
            // 资源服务形态的用户行由令牌投影而来，锁定、两步验证与邮箱验证都归签发方，本服务没有可关注的状态
            Attention = new PlatformAttentionOutputDto()
#endif
        };
    }

    // 时区可用与结束日不晚于今天要用时区与时钟判断，DTO 自身校验不了；与其他字段错误同一通道（400）
    private StatisticsPeriod ResolvePeriod(GetPlatformStatisticsInputDto input, DateTime now)
    {
        if (!userTimeZoneProvider.TryResolve(input.TimeZone, out var timeZone))
        {
            throw InvalidField("The time zone is not a valid IANA time zone.", nameof(GetPlatformStatisticsInputDto.TimeZone));
        }

        var startDate = input.StartDate!.Value;
        var endDate = input.EndDate!.Value;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, timeZone));
        if (endDate > today)
        {
            throw InvalidField("The end date cannot be later than today.", nameof(GetPlatformStatisticsInputDto.EndDate));
        }

        var days = endDate.DayNumber - startDate.DayNumber + 1;
        var start = DayStart(startDate, timeZone);
        var fullEnd = DayStart(endDate.AddDays(1), timeZone);
        var end = fullEnd < now ? fullEnd : now;

        // 上一区间比较相同的本地进度：区间含今天时，终点是此刻的本地钟点前移同样的天数，而不是减 N×24 小时
        var previousStart = DayStart(startDate.AddDays(-days), timeZone);
        var previousEnd = end == fullEnd
            ? DayStart(endDate.AddDays(1 - days), timeZone)
            : ToUtc(TimeZoneInfo.ConvertTimeFromUtc(now, timeZone).AddDays(-days), timeZone);

        var granularity = days == 1 ? StatisticsGranularity.Hour : StatisticsGranularity.Day;
        var edges = new List<DateTime>();
        if (end > start)
        {
            if (granularity == StatisticsGranularity.Hour)
            {
                // 小时桶按真实经过的时间步进：回拨日有 25 个、两个本地 01 点各成一桶，半小时时区的桶起点落在本地 :30
                for (var edge = start; edge < end; edge = edge.AddHours(1))
                {
                    edges.Add(edge);
                }
            }
            else
            {
                for (var date = startDate; DayStart(date, timeZone) is var edge && edge < end; date = date.AddDays(1))
                {
                    edges.Add(edge);
                }
            }

            edges.Add(end);
        }

        return new StatisticsPeriod(
            startDate, endDate, timeZone, granularity, start, end, previousStart, previousEnd, [.. edges]);
    }

    // 某个本地日的第一个时刻
    private static DateTime DayStart(DateOnly date, TimeZoneInfo timeZone)
        => ToUtc(date.ToDateTime(TimeOnly.MinValue), timeZone);

    // 本地时刻换算 UTC：落在跳时缺口里的取缺口之后的第一个时刻，回拨重复的取较早的那一个
    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            // 跳时都发生在整分钟上，从所在的那一分钟往后找到第一个存在的本地时刻
            local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
            while (timeZone.IsInvalidTime(local))
            {
                local = local.AddMinutes(1);
            }
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            var earlierOffset = timeZone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - earlierOffset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    private static ValidationException InvalidField(string message, string member)
        => new(new ValidationResult(message, [member]), validatingAttribute: null, value: null);

    // 某一时刻存在的用户：创建于其前、且当时尚未删除（之后才删除的照样计入）
    private Task<int> CountExistingAsync(IQueryable<User> everCreated, DateTime moment, CancellationToken cancellationToken)
        => asyncExecuter.CountAsync(
            everCreated.Where(user => user.CreationTime < moment && (!user.IsDeleted || user.DeletionTime >= moment)),
            cancellationToken);

    private Task<int> CountCreatedAsync(
        IQueryable<User> everCreated,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
        => asyncExecuter.CountAsync(
            everCreated.Where(user => user.CreationTime >= start && user.CreationTime < end),
            cancellationToken);

    // 分组与计数在数据库里完成；零长度区间没有桶，直接返回空序列
    private async Task<IReadOnlyList<int>> CountByBucketsAsync(
        IQueryable<User> query,
        Expression<Func<User, DateTime>> time,
        DateTime[] edges,
        CancellationToken cancellationToken)
    {
        if (edges.Length < 2)
        {
            return [];
        }

        var rows = await asyncExecuter.ToListAsync(
            query.GroupByTimeBucket(time, edges).Select(group => new { Bucket = group.Key, Count = group.Count() }),
            cancellationToken);
        var counts = new int[edges.Length - 1];
        foreach (var row in rows)
        {
            counts[row.Bucket] = row.Count;
        }

        return counts;
    }
#if (LocalIdentity && IncludeOperationRecords)

    // 本期值与序列来自同一组桶；上期只要总数，用首尾两个边界。零长度区间不查
    private async Task<StatisticValueOutputDto> CountLoginsAsync(
        string action,
        StatisticsPeriod period,
        bool distinctTargets,
        CancellationToken cancellationToken)
    {
        var scope = OperationRecordVisibilityScope.Unrestricted;
        var series = period.Edges.Length < 2
            ? []
            : await operationRecordReader.CountByBucketsAsync(scope, period.Edges, [action], distinctTargets, cancellationToken);
        // 去重的桶数相加不等于区间人数，区间值单独数
        var value = !distinctTargets || period.Edges.Length < 2
            ? series.Sum()
            : (await operationRecordReader.CountByBucketsAsync(
                scope, [period.Start, period.End], [action], distinctTargets: true, cancellationToken))[0];
        var previous = period.PreviousEnd > period.PreviousStart
            ? (await operationRecordReader.CountByBucketsAsync(
                scope, [period.PreviousStart, period.PreviousEnd], [action], distinctTargets, cancellationToken))[0]
            : 0;

        return new StatisticValueOutputDto { Value = value, Previous = previous, Series = series };
    }
#endif
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
                : (int)(await tenantManagementService.GetPagedAsync(
                    new GetTenantPagedInputDto { IsActive = false, Limit = 1 }, cancellationToken)).TotalCount,
#endif
        };
    }
#endif

    // 一次统计的区间：请求的日历日期与时区、换算出的 UTC 范围与上一区间，以及本期的桶边界（零长度区间为空）
    private sealed record StatisticsPeriod(
        DateOnly StartDate,
        DateOnly EndDate,
        TimeZoneInfo TimeZone,
        StatisticsGranularity Granularity,
        DateTime Start,
        DateTime End,
        DateTime PreviousStart,
        DateTime PreviousEnd,
        DateTime[] Edges);
}
