#if (IncludeNotifications)
using Leistd.Notifications.Dtos;
using System.Net;
using System.Net.Http.Json;
using Leistd.BackgroundJobs.Options;
using Leistd.BackgroundJobs.Recurring;
using Leistd.MultiTenancy.Context;
using Leistd.Notifications.EntityFrameworkCore.Entities;
using Leistd.Notifications.EntityFrameworkCore.Options;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Leistd.Notifications.Publishing;
using Leistd.Notifications.AspNetCore.SignalR;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>通知：持久化、经 Hub 推送、已读与清除，以及保留期清理。</summary>
public sealed class NotificationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Notification_retention_cleans_host_and_shared_tenant_rows_at_the_read_and_unread_cutoffs()
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        var clock = new FakeTimeProvider(new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero));
        // 时钟配置变体共用已有数据库；关闭自动调度，只由本用例触发登记的作业。
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<BackgroundJobOptions>(options => options.Enabled = false);
        }));
        var marker = $"retention-{Guid.NewGuid():N}";
        var tenantId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
            var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<NotificationRetentionOptions>>().CurrentValue;
            Assert.True(options.Enabled);
            var now = clock.GetUtcNow().UtcDateTime;
            var readCutoff = now.AddDays(-options.ReadRetentionDays);
            var unreadCutoff = now.AddDays(-options.UnreadRetentionDays);

            foreach (var owner in new Guid?[] { null, tenantId })
            {
                using (tenant.Change(owner))
                {
                    db.Set<NotificationRecord>().AddRange(
                        Row("read-expired", true, readCutoff.AddMilliseconds(-1)),
                        Row("read-equal", true, readCutoff),
                        Row("read-fresh", true, readCutoff.AddMilliseconds(1)),
                        Row("unread-expired", false, unreadCutoff.AddMilliseconds(-1)),
                        Row("unread-equal", false, unreadCutoff),
                        Row("unread-fresh", false, unreadCutoff.AddMilliseconds(1)));
                    await db.SaveChangesAsync();
                }
            }

            var definition = scope.ServiceProvider.GetServices<RecurringJobDefinition>()
                .Single(job => job.Name == "notifications.retention");
            Assert.Equal(RecurringJobScope.Cluster, definition.Scope);
            var job = (IRecurringJob)scope.ServiceProvider.GetRequiredService(definition.JobType);
            await job.ExecuteAsync(new RecurringJobContext(definition.Name, clock.GetUtcNow()), CancellationToken.None);
        }

        await using var verify = host.Services.CreateAsyncScope();
        var rows = await verify.ServiceProvider.GetRequiredService<MyProjectDbContext>().Set<NotificationRecord>()
            .IgnoreQueryFilters().Where(record => record.UserId == marker).ToArrayAsync();
        Assert.Equal(8, rows.Length);
        foreach (var owner in new Guid?[] { null, tenantId })
        {
            Assert.Equal(["read-equal", "read-fresh", "unread-equal", "unread-fresh"],
                rows.Where(row => row.TenantId == owner).Select(row => row.Title).Order(StringComparer.Ordinal));
        }

        NotificationRecord Row(string title, bool read, DateTime created) => new()
        {
            UserId = marker, Title = title, IsRead = read, CreationTime = created
        };
    }

    [Fact]
    public async Task Notification_should_be_persisted_pushed_marked_as_read_and_cleared()
    {
        // 发布器隔离渠道故障、只记日志，推送没到时失败信息里要带上服务端日志，否则无从判断是没发还是没收到
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // 宿主用 Serilog 接管了日志工厂，换回标准工厂才收得到（只影响这个派生宿主）
            services.RemoveAll<ILoggerFactory>();
            services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Warning)
                // 操作记录写日志时，启动期检查要求该类别在 Information 可用
                .AddFilter("Leistd.OperationRecords", LogLevel.Information));
        }));
#if (LocalIdentity)
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var userId = await GetSuperAdminIdAsync(host);
#else
        var userId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        using var admin = ProjectWebApplicationFactory.CreateResourceSession(host, userId, tenantId);
#endif
        // 有业务实时时与业务事件共用实时 Hub，否则走通知自己的 Hub：同一条连接上收到通知，且只收到一次
        await using var connection = CreateHubConnection(host, ProjectWebApplicationFactory.HubPath, admin);
        Exception? closedError = null;
        connection.Closed += error =>
        {
            closedError = error;
            return Task.CompletedTask;
        };
        var received = new TaskCompletionSource<NotificationOutputDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;
        using var subscription = connection.On<NotificationOutputDto>(NotificationClientMethods.Received, notification =>
        {
            Interlocked.Increment(ref deliveries);
            received.TrySetResult(notification);
        });
        await connection.StartAsync();

        var notification = new NotificationInputDto
        {
            Title = "Integration notification",
            Content = "Notification persistence and SignalR delivery"
        };
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
#if (LocalIdentity)
            await publisher.PublishToUserAsync(userId.ToString(), notification);
#else
            // NotificationRecord 实现 IMultiTenant：TenantId 由基座在 SaveChanges 时按**当前租户
            // 上下文**落值，发布器与存储自身都不带租户。这里是独立 scope，没有 HTTP 请求，不切租户
            // 就会落成宿主行；而下面用租户会话查询时会被全局过滤器滤掉，表现为"推送收到了、未读数
            // 却是 0"——推送按 userId 直达，不受过滤器约束，所以只有查询这一侧会露馅。
            // 因此发布方负责建立租户上下文。本用例连同这条契约一起钉住，而不只是让断言变绿。
            using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
            {
                await publisher.PublishToUserAsync(userId.ToString(), notification);
            }
#endif
        }

        NotificationOutputDto pushed;
        try
        {
            pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException exception)
        {
            var unread = await admin.Client.GetFromJsonAsync<int>("/api/v1/notifications/unread-count");
            throw new TimeoutException(
                $"No notification push within 10s. Connection={connection.State}, Closed={closedError?.Message ?? "none"}, " +
                $"Deliveries={Volatile.Read(ref deliveries)}, Unread={unread}.{Environment.NewLine}Server log:{Environment.NewLine}{ServerWarnings(host)}",
                exception);
        }
        Assert.Equal(1, await admin.Client.GetFromJsonAsync<int>("/api/v1/notifications/unread-count"));
        Assert.Equal(1, Volatile.Read(ref deliveries));

        // 身份由发布器在收件人边界定案，调用方并不知道它——因此这里断言的是那条保证本身：
        // 推送里的 ID 必须就是落库那条的 ID，否则客户端拿推送的 ID 去标记已读会命不中。
        var notifications = await admin.Client.GetFromJsonAsync<List<NotificationOutputDto>>("/api/v1/notifications");
        var stored = Assert.Single(notifications!);
        Assert.Equal(stored.Id, pushed.Id);
        Assert.False(stored.IsRead);
        Assert.Equal(notification.Title, stored.Title);
        var notificationId = stored.Id;

        var markRead = await admin.Client.PutAsync($"/api/v1/notifications/{notificationId}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, markRead.StatusCode);
        Assert.Equal(0, await admin.Client.GetFromJsonAsync<int>("/api/v1/notifications/unread-count"));

        // 删除单条：持久删除指定通知
        var clearOne = await admin.Client.DeleteAsync($"/api/v1/notifications/{notificationId}");
        Assert.Equal(HttpStatusCode.NoContent, clearOne.StatusCode);

        var afterClearOne = await admin.Client.GetFromJsonAsync<List<NotificationOutputDto>>("/api/v1/notifications");
        Assert.DoesNotContain(afterClearOne!, item => item.Id == notificationId);

        // 清空全部：持久删除当前用户的通知记录
        var clearAll = await admin.Client.DeleteAsync("/api/v1/notifications");
        Assert.Equal(HttpStatusCode.NoContent, clearAll.StatusCode);

        var afterClear = await admin.Client.GetFromJsonAsync<List<NotificationOutputDto>>("/api/v1/notifications");
        Assert.Empty(afterClear!);

        // 显式停止连接，排空 SignalR 后台循环（持有 CTS），避免与 await using 释放竞争导致类清理期 ObjectDisposedException
        await connection.StopAsync();
    }

    private static HubConnection CreateHubConnection(
        WebApplicationFactory<Program> application,
        string path,
        AuthenticatedSession session)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(application.Server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => application.Server.CreateHandler();
                if (!string.IsNullOrEmpty(session.Cookie))
                    options.Headers.Add("Cookie", session.Cookie);
                foreach (var (name, value) in session.AuthenticationHeaders)
                    options.Headers.Add(name, value);
            })
            .Build();
    }

    // 带上异常全文：被隔离吞掉的渠道故障只剩这一处线索
    private static string ServerWarnings(WebApplicationFactory<Program> host)
    {
        var records = host.Services.GetFakeLogCollector().GetSnapshot();
        return records.Count == 0
            ? "(no warnings or errors logged)"
            : string.Join(Environment.NewLine, records.Select(record =>
                $"[{record.Category}] {record.Message}{(record.Exception is null ? string.Empty : Environment.NewLine + record.Exception)}"));
    }
#if (LocalIdentity)

    private static async Task<Guid> GetSuperAdminIdAsync(WebApplicationFactory<Program> application)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return await db.Users.Where(user => user.IsSuperAdmin).Select(user => user.Id).SingleAsync();
    }
#endif
}
#endif
