using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>启动期<b>只校验不施加</b>迁移：有待执行迁移就失败并打印清单。</summary>
/// <remarks>
/// API 使用只具备 DML 权限的 Runtime 身份，DDL 由持有 Migration 身份的 DbMigrator
/// 独占执行。启动校验在接收流量前检查所有上下文；存在待执行迁移时立即失败并列出清单。
/// </remarks>
public static class DatabaseSchemaVerificationExtensions
{
    /// <summary>校验各上下文的迁移已全部施加。放在 <c>app.Run()</c> 之前。</summary>
    public static async Task VerifyDatabaseSchemaAsync(this IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        await VerifyAsync(services.GetRequiredService<MyProjectDbContext>(), "business");
#if (LocalIdentity && IncludeMultiTenancy)
        await VerifyAsync(services.GetRequiredService<IdentityControlDbContext>(), "control");
#endif
#if (OpenIddictServer)
        await VerifyAsync(services.GetRequiredService<OpenIddictDbContext>(), "openiddict");
#endif

        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseSchemaVerificationExtensions))
            .LogInformation("Database schema is up to date.");
    }

    private static async Task VerifyAsync(DbContext dbContext, string scope)
    {
        var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        // 清单必须进错误消息：只说"有待执行迁移"会让运维不知道该不该继续发布
        throw new InvalidOperationException(
            $"The {scope} database has {pending.Count} pending migration(s): {string.Join(", ", pending)}. " +
            "Run CompanyName.ProjectName.DbMigrator --apply before starting the API.");
    }
}
