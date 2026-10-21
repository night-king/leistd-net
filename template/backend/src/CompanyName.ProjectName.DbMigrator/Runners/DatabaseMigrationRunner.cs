using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.MultiTenancy.ConnectionStrings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Leistd.Data.Connections;

namespace CompanyName.ProjectName.DbMigrator.Runners;

/// <summary>预演或施加所有物理数据库目标的 EF Core 迁移。</summary>
/// <remarks>
/// 默认只输出计划和 SQL；显式施加时由 EF Core 管理迁移锁和事务，不得再包裹外层事务。
/// 日志和报告使用连接串的 SHA256 指纹标识目标。
/// </remarks>
public sealed class DatabaseMigrationRunner(
    IConfiguration configuration,
#if (IncludeMultiTenancy)
    ITenantMigrationTargetProvider targetProvider,
#endif
    ILogger<DatabaseMigrationRunner> logger)
{
    /// <summary>业务上下文的连接名，即租户连接登记里要查的名字。</summary>
    /// <remarks>
    /// <c>MyProjectDbContext</c> 没有声明 <c>[ConnectionStringName]</c>，因此用默认名。
    /// 业务项目把上下文改名为服务名（如 <c>Crm</c>）时，这里要跟着改成同一个名字——
    /// 迁移作业与运行时必须问同一个名字，否则会迁错库。
    /// </remarks>
    private const string BusinessConnectionStringName = ConnectionStringNames.Default;

    /// <summary>表示一个物理目标的迁移计划。</summary>
    /// <param name="Target">连接串指纹或 <c>default</c>。</param>
    /// <param name="Scope">同一目标上的模型范围。</param>
    /// <param name="PendingMigrations">待执行迁移；空集合表示已是最新。</param>
    /// <param name="Script">预演生成的 SQL；施加时为空字符串。</param>
    public sealed record MigrationPlan(
        string Target,
        string Scope,
        IReadOnlyList<string> PendingMigrations,
        string Script);

    /// <summary>表示共享同一模型范围和预演 SQL 的目标组。</summary>
    /// <param name="Scope">模型范围。</param>
    /// <param name="Script">该组共用的 SQL。</param>
    /// <param name="Targets">适用的目标标识。</param>
    public sealed record MigrationScriptGroup(
        string Scope,
        string Script,
        IReadOnlyList<string> Targets);

    /// <summary>按模型范围和 SQL 合并预演计划。</summary>
    /// <remarks>
    /// 只有实际执行相同 SQL 的目标才会合并。
    /// </remarks>
    internal static IReadOnlyList<MigrationScriptGroup> GroupScripts(IEnumerable<MigrationPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        return plans
            .Where(x => !string.IsNullOrEmpty(x.Script))
            .GroupBy(x => (x.Scope, x.Script))
            .Select(g => new MigrationScriptGroup(
                g.Key.Scope,
                g.Key.Script,
                g.Select(x => x.Target).ToArray()))
            .ToArray();
    }

#if (IncludeMultiTenancy)
    /// <summary>取不出连接的租户：它的库没有可迁的目标。</summary>
    /// <param name="TenantId">租户。</param>
    /// <param name="Reason">原因；不含连接串。</param>
    public sealed record UnresolvedTenant(Guid TenantId, string Reason);

    /// <summary>迁移没有完成的租户库；它可能已提交了前几条迁移（EF 按迁移各自提交）。</summary>
    /// <param name="Target">连接串指纹。</param>
    /// <param name="TenantId">代表租户：共用这个库的租户都受影响，不只是它。</param>
    /// <param name="Reason">异常类型与消息；不含连接串。</param>
    public sealed record FailedTarget(string Target, Guid TenantId, string Reason);

    /// <summary>表示所有物理目标的迁移报告。</summary>
    /// <param name="Plans">完成预演或施加的目标的计划。</param>
    /// <param name="UnresolvedTenants">取不出连接、因而没有迁移的租户。</param>
    /// <param name="FailedTargets">迁移执行失败的租户库。</param>
    /// <remarks>
    /// 有任何一项失败即为未成功：其余租户库照常迁移（一个租户的问题不挡住其他租户），
    /// 但作业必须以失败结束，不能让那些库悄悄停在旧结构上。
    /// 控制库、OIDC 库与默认库的失败不在这里，它们直接抛出、结束本次运行。
    /// </remarks>
    public sealed record MigrationReport(
        IReadOnlyList<MigrationPlan> Plans,
        IReadOnlyList<UnresolvedTenant> UnresolvedTenants,
        IReadOnlyList<FailedTarget> FailedTargets)
    {
        /// <summary>全部目标都已预演或施加。</summary>
        public bool Succeeded => UnresolvedTenants.Count == 0 && FailedTargets.Count == 0;
    }
#else
    /// <summary>表示所有物理目标的完整迁移报告。</summary>
    /// <param name="Plans">各物理目标的计划。</param>
    public sealed record MigrationReport(IReadOnlyList<MigrationPlan> Plans);
#endif

    /// <summary>计算所有物理目标的计划，并按 <paramref name="apply"/> 决定是否施加迁移。</summary>
    /// <param name="apply"><see langword="false"/> 时只输出清单和 SQL。</param>
    /// <remarks>
    /// 首次安装缺少租户注册表时按无独立目标处理；其他枚举失败均上抛。
    /// </remarks>
    public async Task<MigrationReport> RunAsync(
        bool apply,
        CancellationToken cancellationToken = default)
    {
        var plans = new List<MigrationPlan>();

        var explicitTarget = configuration.GetConnectionString("MigrationTarget");
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            var target = new TenantMigrationTarget(Guid.Empty, explicitTarget);
            plans.Add(await ProcessBusinessAsync(explicitTarget, target.Fingerprint, apply, cancellationToken));
            return Report(plans);
        }

        var defaultConnection = configuration.GetConnectionString(ConnectionStringNames.Default);
        if (string.IsNullOrWhiteSpace(defaultConnection))
        {
            throw new InvalidOperationException("ConnectionStrings:Default is required by DbMigrator.");
        }

#if ((LocalIdentity && IncludeMultiTenancy) || OpenIddictServer)
        // 使用与运行时一致的控制面连接回退链。
        var controlConnection = configuration.GetControlPlaneConnectionString() ?? defaultConnection;

        // 同库沿用 default，独立控制库使用指纹标识。
        var controlTarget = string.Equals(controlConnection, defaultConnection, StringComparison.Ordinal)
            ? "default"
            : new TenantMigrationTarget(Guid.Empty, controlConnection).Fingerprint;
#endif

#if (IncludeMultiTenancy)
        MigrationPlan? controlPlan = null;
#endif
#if (LocalIdentity && IncludeMultiTenancy)
        controlPlan = await ProcessControlAsync(controlConnection, controlTarget, apply, cancellationToken);
        plans.Add(controlPlan);
#endif
#if (OpenIddictServer)
        // OIDC 与控制面同库，但有独立上下文和迁移历史。
        plans.Add(await ProcessOpenIddictAsync(controlConnection, controlTarget, apply, cancellationToken));
#endif
        plans.Add(await ProcessBusinessAsync(defaultConnection, "default", apply, cancellationToken));

#if (IncludeMultiTenancy)
        // 始终真实枚举目标；只有已证实的首次安装可将缺表视为无独立目标。
        // 读不出清单本身（控制库、回源失败）必须非零退出，防止不完整预演随后施加额外目标。
        TenantMigrationTargetSet targets;
        try
        {
            // 用本服务业务上下文的连接名问：租户在这个名字下没登记，就回落到它的默认名登记；
            // 两者都没有、或连接串解不开的租户单列在 FailedTenants 里，报出来而不是被静默跳过
            targets = await targetProvider.GetDedicatedTargetsAsync(
                BusinessConnectionStringName,
                cancellationToken);
        }
        catch (Exception exception) when (IsFirstInstall(exception, apply, controlPlan))
        {
            logger.LogInformation(
                "The tenant registry does not exist yet and the control database has not been migrated; " +
                "treating this run as a first install with no dedicated targets.");
            return Report(plans);
        }

        // 提供器已按连接指纹去重（运行时逐库作业用的是另一份不带连接串的清单，去重口径相同）。
        // 一个租户库连不上或迁移出错只记在它名下，其余库照常迁移；取消立即传播，不当成某个库的失败继续执行 DDL
        var failedTargets = new List<FailedTarget>();
        foreach (var target in targets.Targets)
        {
            try
            {
                plans.Add(await ProcessBusinessAsync(target.ConnectionString, target.Fingerprint, apply, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "Migrating tenant database {Target} (tenant {TenantId}) failed; continuing with the remaining targets.",
                    target.Fingerprint, target.TenantId);
                failedTargets.Add(new FailedTarget(
                    target.Fingerprint, target.TenantId, $"{exception.GetType().Name}: {exception.Message}"));
            }
        }

        return new MigrationReport(
            plans,
            [.. targets.FailedTenants.Select(failure => new UnresolvedTenant(failure.TenantId, failure.Reason))],
            failedTargets);
#else
        return Report(plans);
#endif
    }

#if (IncludeMultiTenancy)
    private static MigrationReport Report(List<MigrationPlan> plans) => new(plans, [], []);
#else
    private static MigrationReport Report(List<MigrationPlan> plans) => new(plans);
#endif

#if (LocalIdentity && IncludeMultiTenancy)
    private async Task<MigrationPlan> ProcessControlAsync(
        string connectionString,
        string target,
        bool apply,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<IdentityControlDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(
                    DatabaseSchema.ControlMigrationsHistoryTable, DatabaseSchema.Name));

        await using var dbContext = new IdentityControlDbContext(options.Options, serviceProvider: null);
        return await ProcessAsync(dbContext, target, "control", apply, cancellationToken);
    }
#endif

#if (OpenIddictServer)
    private async Task<MigrationPlan> ProcessOpenIddictAsync(
        string connectionString,
        string target,
        bool apply,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<OpenIddictDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(
                    OpenIddictDbContext.MigrationsHistoryTable, DatabaseSchema.Name))
            .UseOpenIddict();

        await using var dbContext = new OpenIddictDbContext(options.Options);
        return await ProcessAsync(dbContext, target, "openiddict", apply, cancellationToken);
    }
#endif

    private async Task<MigrationPlan> ProcessBusinessAsync(
        string connectionString,
        string targetFingerprint,
        bool apply,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<MyProjectDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(
                    DatabaseSchema.BusinessMigrationsHistoryTable, DatabaseSchema.Name));

        await using var dbContext = new MyProjectDbContext(options.Options, serviceProvider: null);
        return await ProcessAsync(dbContext, targetFingerprint, "business", apply, cancellationToken);
    }

    // 仅只读预演中未迁移的控制库缺少租户表时，才判定为首次安装。
    private static bool IsFirstInstall(Exception exception, bool apply, MigrationPlan? controlPlan)
    {
        if (apply)
        {
            return false;
        }

        if (controlPlan is null)
        {
            return false;
        }

        if (controlPlan.PendingMigrations.Count == 0)
        {
            return false;
        }

        return IsMissingTenantRegistry(exception);
    }

    private static bool IsMissingTenantRegistry(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "42P01" })
            {
                return true;
            }
        }

        return false;
    }

    private async Task<MigrationPlan> ProcessAsync(
        DbContext dbContext,
        string target,
        string scope,
        bool apply,
        CancellationToken cancellationToken)
    {
        // 读取待执行迁移不会创建历史表，适合安全预演。
        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation("Target {DatabaseTarget} ({Scope}) is up to date.", target, scope);
            return new MigrationPlan(target, scope, pending, string.Empty);
        }

        if (!apply)
        {
            logger.LogInformation(
                "Target {DatabaseTarget} ({Scope}) has {PendingCount} pending migration(s); no change applied.",
                target, scope, pending.Count);

            // 从最后一个已施加迁移生成脚本；首次迁移从初始状态生成。
            var applied = await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken);
            var script = dbContext.GetService<IMigrator>().GenerateScript(
                fromMigration: applied.LastOrDefault(),
                toMigration: null,
                options: MigrationsSqlGenerationOptions.Default);

            return new MigrationPlan(target, scope, pending, script);
        }

        logger.LogInformation(
            "Applying {PendingCount} migration(s) to target {DatabaseTarget} ({Scope}).",
            pending.Count, target, scope);

        // EF Core 自行管理迁移锁和事务，不能包裹外层事务。
        await dbContext.Database.MigrateAsync(cancellationToken);
        return new MigrationPlan(target, scope, pending, string.Empty);
    }
}
