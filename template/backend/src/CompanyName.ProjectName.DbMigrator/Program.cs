using CompanyName.ProjectName.DbMigrator;
using CompanyName.ProjectName.DbMigrator.Runners;
#if (RemoteTokenAuth || LocalIdentity)
using Leistd.DependencyInjection.DynamicProxy.Registration;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 默认只读预演，--apply 才写库。迁移是不可逆的生产数据变更，
// "跑一下看看"和"改掉生产库"不能是同一个动作。
var apply = false;
#if (RemoteTokenAuth)
Guid? adminSubject = null;
Guid? adminTenant = null;
var restore = false;
#endif
#if (LocalIdentity && IncludeMultiTenancy)
Guid? restoreTenant = null;
#endif
#if (LocalIdentity)
Guid? resetTwoFactorUser = null;
Guid? resetTwoFactorTenant = null;
#endif
for (var index = 0; index < args.Length; index++)
{
    var argument = args[index];
    switch (argument)
    {
        case "--apply":
            apply = true;
            break;

#if (RemoteTokenAuth)
        case "--grant-admin":
        case "--tenant":
            if (index + 1 >= args.Length || !Guid.TryParse(args[++index], out var id) || id == Guid.Empty)
            {
                Console.Error.WriteLine($"{argument} requires a non-empty GUID.");
                return 2;
            }
            if (argument == "--grant-admin")
            {
                if (adminSubject.HasValue) { Console.Error.WriteLine("Duplicate --grant-admin."); return 2; }
                adminSubject = id;
            }
            else
            {
#if (IncludeMultiTenancy)
                if (adminTenant.HasValue) { Console.Error.WriteLine("Duplicate --tenant."); return 2; }
                adminTenant = id;
#else
                Console.Error.WriteLine("--tenant is unavailable in a host-only application.");
                return 2;
#endif
            }
            break;

        case "--restore":
            restore = true;
            break;
#endif
#if (LocalIdentity && IncludeMultiTenancy)
        case "--restore-tenant-admin":
            if (index + 1 >= args.Length || !Guid.TryParse(args[++index], out var tenantId) || tenantId == Guid.Empty)
            {
                Console.Error.WriteLine($"{argument} requires a non-empty tenant GUID.");
                return 2;
            }
            if (restoreTenant.HasValue) { Console.Error.WriteLine("Duplicate --restore-tenant-admin."); return 2; }
            restoreTenant = tenantId;
            break;
#endif
#if (LocalIdentity)
        case "--reset-two-factor":
        case "--tenant":
            if (index + 1 >= args.Length || !Guid.TryParse(args[++index], out var resetId) || resetId == Guid.Empty)
            {
                Console.Error.WriteLine($"{argument} requires a non-empty GUID.");
                return 2;
            }
            if (argument == "--reset-two-factor")
            {
                if (resetTwoFactorUser.HasValue) { Console.Error.WriteLine("Duplicate --reset-two-factor."); return 2; }
                resetTwoFactorUser = resetId;
            }
            else
            {
#if (IncludeMultiTenancy)
                if (resetTwoFactorTenant.HasValue) { Console.Error.WriteLine("Duplicate --tenant."); return 2; }
                resetTwoFactorTenant = resetId;
#else
                Console.Error.WriteLine("--tenant is unavailable in a host-only application.");
                return 2;
#endif
            }
            break;
#endif
        case "--help" or "-h":
            WriteUsage();
            return 0;

        default:
            // 不静默忽略：--aply 这类手误若被当成预演，本意是施加的人会以为已经施加了
            Console.Error.WriteLine($"Unknown argument: {argument}");
            WriteUsage();
            return 2;
    }
}

#if (RemoteTokenAuth)
if (adminTenant.HasValue && !adminSubject.HasValue)
{
    Console.Error.WriteLine("--tenant requires --grant-admin.");
    return 2;
}
if (restore && !adminSubject.HasValue)
{
    Console.Error.WriteLine("--restore requires --grant-admin.");
    return 2;
}
#endif
#if (LocalIdentity)
if (resetTwoFactorTenant.HasValue && !resetTwoFactorUser.HasValue)
{
    Console.Error.WriteLine("--tenant requires --reset-two-factor.");
    return 2;
}
#endif
#if (LocalIdentity && IncludeMultiTenancy)
if (resetTwoFactorUser.HasValue && restoreTenant.HasValue)
{
    Console.Error.WriteLine("--reset-two-factor and --restore-tenant-admin are separate commands; run them one at a time.");
    return 2;
}
#endif
// 刻意不把 args 交给宿主：命令行配置提供程序会把 `--apply`（无值开关）当配置键解析。
// 本作业的配置来自环境变量与 appsettings——K8s Job 的标准做法，不需要命令行覆盖。
var builder = Host.CreateApplicationBuilder();
#if (RemoteTokenAuth || LocalIdentity)
// 管理员命令走正式业务路径，要织入工作单元等拦截器；与 API 一样，生产以外都校验作用域与构建期依赖
var validateServiceProvider = !builder.Environment.IsProduction();
var containerFactory = new DynamicProxyServiceRegistrationCallbackFactory(new ServiceProviderOptions
{
    ValidateScopes = validateServiceProvider,
    ValidateOnBuild = validateServiceProvider
});
#endif
#if (RemoteTokenAuth)
if (adminSubject.HasValue)
{
    builder.ConfigureContainer(containerFactory);
    builder.Services.AddResourceAdminBootstrapServices(builder.Configuration);
}
else
#endif
#if (LocalIdentity)
if (resetTwoFactorUser.HasValue)
{
    builder.ConfigureContainer(containerFactory);
    builder.Services.AddTwoFactorResetServices(builder.Configuration, builder.Environment);
}
else
#endif
#if (LocalIdentity && IncludeMultiTenancy)
if (restoreTenant.HasValue)
{
    builder.ConfigureContainer(containerFactory);
    builder.Services.AddTenantAdminRestoreServices(builder.Configuration, builder.Environment);
}
else
#endif
{
    // 迁移后的 Admin 补齐同样走正式写入路径
    builder.ConfigureContainer(containerFactory);
    builder.Services.AddMigratorServices(builder.Configuration, builder.Environment);
}

try
{
    using var host = builder.Build();
    await using var scope = host.Services.CreateAsyncScope();
#if (RemoteTokenAuth)
    if (adminSubject is { } subject)
    {
        // 启动期闸门（包括必需日志级别）必须在写入前成立。
        await host.StartAsync();
        // 收到 SIGTERM 等停止信号时取消等锁与提交前的写库，作业以非零码退出；提交一旦开始不再响应取消，可能照常完成
        var result = await scope.ServiceProvider.GetRequiredService<ResourceAdminBootstrapRunner>()
            .RunAsync(subject, adminTenant, apply, restore, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        Console.WriteLine(result);
        await host.StopAsync();
        return 0;
    }
#endif
#if (LocalIdentity)
    if (resetTwoFactorUser is { } resetUser)
    {
        // 启动期闸门（包括必需日志级别）必须在写入前成立。
        await host.StartAsync();
        // 收到 SIGTERM 等停止信号时取消等锁与提交前的写库，作业以非零码退出；提交一旦开始不再响应取消，可能照常完成
        var result = await scope.ServiceProvider.GetRequiredService<TwoFactorResetRunner>()
            .RunAsync(resetUser, resetTwoFactorTenant, apply, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        Console.WriteLine(result);
        await host.StopAsync();
        return 0;
    }
#endif
#if (LocalIdentity && IncludeMultiTenancy)
    if (restoreTenant is { } tenant)
    {
        // 启动期闸门（包括必需日志级别）必须在写入前成立。
        await host.StartAsync();
        // 收到 SIGTERM 等停止信号时取消等锁与提交前的写库，作业以非零码退出；提交一旦开始不再响应取消，可能照常完成
        var result = await scope.ServiceProvider.GetRequiredService<TenantAdminRestoreRunner>()
            .RunAsync(tenant, apply, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        Console.WriteLine(result);
        await host.StopAsync();
        return 0;
    }
#endif
    var report = await scope.ServiceProvider
        .GetRequiredService<DatabaseMigrationRunner>()
        .RunAsync(apply);

    WriteReport(report, apply);
#if (IncludeMultiTenancy)
    if (!report.Succeeded)
    {
        Console.WriteLine("Admin permission top-up skipped: fix the failed targets above and run again.");
        return 1;
    }
#endif
    // 单库模式（MigrationTarget）只为一个库建表，不枚举其他库，也不补齐
    if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("MigrationTarget")))
    {
        return 0;
    }

    // 预演时有待执行的迁移：授权表可能还不存在或结构未更新，补齐在施加之后才能算准
    if (!apply && report.Plans.Any(plan => plan.PendingMigrations.Count > 0))
    {
        Console.WriteLine("Admin permission top-up is checked after the pending migrations are applied.");
        return 0;
    }

    var topUp = await scope.ServiceProvider
        .GetRequiredService<AdminGrantTopUpRunner>()
        .RunAsync(apply);
    WriteTopUpReport(topUp, apply);
    return topUp.Succeeded ? 0 : 1;
}
catch (OperationCanceledException)
{
    // 取消可能发生在任何阶段（含提交之后的分发），这里不推断是否已写入，由运维按实际状态核实
    Console.Error.WriteLine("The command was cancelled. Check the current state before running it again.");
    return 1;
}
catch (Exception exception)
{
    // 只写类型名不足以定位：迁移失败时运维需要知道是哪个目标、哪一条迁移。
    // 连接串不会出现在这里——目标以 SHA256 指纹标识，解密失败的消息也不含明文。
    Console.Error.WriteLine($"Database command failed: {exception.GetType().Name}: {exception.Message}");
    for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
    {
        Console.Error.WriteLine($"  caused by {inner.GetType().Name}: {inner.Message}");
    }

    return 1;
}

static void WriteUsage()
{
    Console.WriteLine("Usage: dotnet CompanyName.ProjectName.DbMigrator.dll [--apply]");
    Console.WriteLine();
    Console.WriteLine("  (no argument)  Dry run: report pending migrations and the SQL, change nothing.");
    Console.WriteLine("  --apply        Apply the pending migrations.");
#if (RemoteTokenAuth)
#if (IncludeMultiTenancy)
    Console.WriteLine("  --grant-admin <sub> [--tenant <id>]  Preview first administrator grants; add --apply to persist.");
#else
    Console.WriteLine("  --grant-admin <sub>  Preview first administrator grants; add --apply to persist.");
#endif
    Console.WriteLine("  --restore      With --grant-admin: also restore a deleted, disabled or removed administrator");
    Console.WriteLine("                 and re-grant the shared Admin role's missing permissions.");
#endif
#if (LocalIdentity)
#if (IncludeMultiTenancy)
    Console.WriteLine("  --reset-two-factor <user-id> [--tenant <id>]  Preview turning off a user's two-factor authentication");
#else
    Console.WriteLine("  --reset-two-factor <user-id>  Preview turning off a user's two-factor authentication");
#endif
    Console.WriteLine("                 (secret, recovery codes and sessions) when no one can reset it online; add --apply to persist.");
#endif
#if (LocalIdentity && IncludeMultiTenancy)
    Console.WriteLine("  --restore-tenant-admin <tenant-id>  Preview restoring the tenant's original administrator account,");
    Console.WriteLine("                 its Admin membership and the shared Admin role's missing permissions; add --apply to persist.");
#endif
}

static void WriteTopUpReport(AdminGrantTopUpRunner.TopUpReport report, bool apply)
{
    Console.WriteLine(apply ? "=== Admin permissions topped up ===" : "=== Dry run: Admin permissions to top up ===");
    foreach (var failure in report.Failures)
    {
        Console.WriteLine($"  FAILED {Scope(failure.TenantId)}: {failure.Reason}");
    }

    var gaps = report.Admins.Where(admin => admin.Permissions.Count > 0).ToList();
    if (gaps.Count == 0)
    {
        Console.WriteLine($"All {report.Admins.Count} Admin role(s) already hold every available permission.");
        return;
    }

    foreach (var admin in gaps)
    {
        Console.WriteLine($"  {Scope(admin.TenantId)}: {(apply ? "granted" : "would grant")} {admin.Permissions.Count}: {string.Join(", ", admin.Permissions)}");
    }

    static string Scope(Guid? tenantId) => tenantId is { } id ? $"tenant {id}" : "host";
}

static void WriteReport(DatabaseMigrationRunner.MigrationReport report, bool apply)
{
    var plans = report.Plans;
    var pendingPlans = plans.Where(x => x.PendingMigrations.Count > 0).ToList();

    Console.WriteLine(apply ? "=== Migrations applied ===" : "=== Dry run: pending migrations ===");
#if (IncludeMultiTenancy)
    // 失败先报：它们决定退出码，而且不能被后面"已是最新"之类的结论盖住
    WriteFailures(report);
#endif

    if (pendingPlans.Count == 0)
    {
#if (IncludeMultiTenancy)
        Console.WriteLine(report.Succeeded
            ? $"All {plans.Count} target(s) are up to date. Nothing to do."
            : $"The {plans.Count} reachable target(s) are up to date; the failures above did not complete.");
#else
        Console.WriteLine($"All {plans.Count} target(s) are up to date. Nothing to do.");
#endif
        return;
    }

    foreach (var plan in pendingPlans)
    {
        Console.WriteLine($"[{plan.Scope}] target {plan.Target}: {plan.PendingMigrations.Count} migration(s)");
        foreach (var migration in plan.PendingMigrations)
        {
            Console.WriteLine($"    {migration}");
        }
    }

    if (apply)
    {
        return;
    }

    // SQL 按「scope + 脚本内容」去重输出，并列出每组适用的目标——
    // 分组键为什么必须含脚本本身，见 DatabaseMigrationRunner.GroupScripts
    Console.WriteLine();
    foreach (var group in DatabaseMigrationRunner.GroupScripts(pendingPlans))
    {
        Console.WriteLine($"--- SQL for scope '{group.Scope}' ({group.Targets.Count} target(s)) ---");
        foreach (var target in group.Targets)
        {
            Console.WriteLine($"    applies to target {target}");
        }

        Console.WriteLine(group.Script);
    }

#if (IncludeMultiTenancy)
    Console.WriteLine(report.Succeeded
        ? "Dry run complete. No change was applied. Re-run with --apply to execute."
        : "Dry run complete. No change was applied. Fix the failures above before re-running with --apply.");
#else
    Console.WriteLine("Dry run complete. No change was applied. Re-run with --apply to execute.");
#endif
}
#if (IncludeMultiTenancy)

// 其余库已经迁移（或预演）完；这些没有完成。失败的库可能已提交了前几条迁移（EF 按迁移各自提交），
// 先看原因与它的迁移历史，修好之后重跑本作业——已施加的迁移不会重复执行
static void WriteFailures(DatabaseMigrationRunner.MigrationReport report)
{
    if (report.Succeeded)
    {
        return;
    }

    foreach (var tenant in report.UnresolvedTenants)
    {
        Console.Error.WriteLine($"Tenant {tenant.TenantId} has no migration target: {tenant.Reason}");
    }

    foreach (var target in report.FailedTargets)
    {
        Console.Error.WriteLine($"Target {target.Target} (tenant {target.TenantId}) did not complete: {target.Reason}");
    }

    Console.Error.WriteLine(
        $"{report.UnresolvedTenants.Count} tenant(s) without a target were not migrated, and {report.FailedTargets.Count} target(s) " +
        "did not complete (they may hold some of the pending migrations). Fix them and run the migrator again.");
}
#endif
