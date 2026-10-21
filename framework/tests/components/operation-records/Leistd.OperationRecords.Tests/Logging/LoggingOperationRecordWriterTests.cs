using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.EventBus.Local;
using Leistd.EventBus.Abstractions;
using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Leistd.MultiTenancy;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.EntityFrameworkCore;
using Leistd.OperationRecords.Logging;
using Leistd.OperationRecords.Logging.Constants;
using Leistd.OperationRecords.Logging.Registration;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Queries;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.Security;
using Leistd.TestBase.Assertions;
using Leistd.Timing;
using Leistd.Tracing;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.OperationRecords.Tests.Logging;

/// <summary>日志输出模式：成功记录只在事务真正提交之后写出，失败记录立即写出。</summary>
/// <remarks>
/// 断言的是调用方能观察到的后果——日志里出现了什么、什么时候出现、带着哪些字段——
/// 用真实的工作单元、本地事件总线与拦截器织入，而不是替身。
/// </remarks>
public sealed class LoggingOperationRecordWriterTests
{
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task A_succeeded_record_is_written_only_after_the_unit_of_work_commits()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();

        using (var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            await RecordSucceededAsync(provider, "user.created");
            Assert.Empty(Records(logs));

            await uow.CompleteAsync();
        }

        var record = Assert.Single(Records(logs));
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Equal(OperationRecordLogging.SucceededEventId, record.Id.Id);
        Assert.Equal("user.created", Field(record, "OperationAction"));
        Assert.Equal("Succeeded", Field(record, "OperationOutcome"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_earlier_after_commit_failure_does_not_skip_success_records_or_delivery_reports(bool sinkFails)
    {
        await using var provider = Build(
            logging =>
            {
                if (sinkFails) logging.AddProvider(new ThrowingLoggerProvider(OperationRecordLogging.CategoryName));
            },
            services => services.AddTransient<IEventHandler<FailingSideEffect>, FailingSideEffectHandler>());
        var logs = provider.GetFakeLogCollector();
        using var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        await provider.GetRequiredService<ILocalEventBus>().PublishAsync(new FailingSideEffect());
        await RecordSucceededAsync(provider, "user.created");
        await RecordSucceededAsync(provider, "user.updated");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => uow.CompleteAsync());

        Assert.Equal("side effect unavailable", failure.Message);
        Assert.True(uow.IsCompleted);
        Assert.Equal(["user.created", "user.updated"], Records(logs).Select(record => Field(record, "OperationAction")));
        Assert.DoesNotContain(Records(logs), record => Field(record, "OperationOutcome") == "Failed");
        var delivery = logs.GetSnapshot().Where(record => record.Category == OperationRecordLogging.DeliveryCategoryName).ToArray();
        Assert.Equal(sinkFails ? 2 : 0, delivery.Length);
        Assert.All(delivery, record =>
        {
            Assert.Equal(LogLevel.Critical, record.Level);
            Assert.Equal(OperationRecordLogging.DeliveryFailedEventId, record.Id.Id);
        });
    }

    [Fact]
    public async Task A_rolled_back_unit_of_work_writes_no_succeeded_record()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();

        using (var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            await RecordSucceededAsync(provider, "user.created");
            await uow.RollbackAsync();
        }

        // 未完成即释放同样是回滚
        using (provider.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            await RecordSucceededAsync(provider, "user.updated");
        }

        Assert.Empty(Records(logs));
    }

    [Fact]
    public async Task A_failed_commit_writes_no_succeeded_record()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();

        using (var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            uow.AddTransactionApi("failing", new FailingCommitTransactionApi());
            await RecordSucceededAsync(provider, "user.created");

            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.CompleteAsync());
        }

        Assert.Empty(Records(logs));
    }

    [Fact]
    public async Task A_nested_unit_of_work_defers_to_the_outer_commit()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using (var outer = manager.Begin())
        {
            using (var inner = manager.Begin())
            {
                await RecordSucceededAsync(provider, "user.created");
                await inner.CompleteAsync();
            }

            Assert.Empty(Records(logs));
            await outer.CompleteAsync();
        }

        Assert.Single(Records(logs));
    }

    [Fact]
    public async Task A_requires_new_unit_of_work_writes_on_its_own_commit_even_if_the_outer_rolls_back()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using (var outer = manager.Begin())
        {
            using (var independent = manager.Begin(requiresNew: true))
            {
                await RecordSucceededAsync(provider, "user.created");
                await independent.CompleteAsync();
            }

            Assert.Single(Records(logs));

            await RecordSucceededAsync(provider, "user.updated");
            await outer.RollbackAsync();
        }

        Assert.Equal(["user.created"], Records(logs).Select(r => Field(r, "OperationAction")));
    }

    [Fact]
    public async Task A_non_transactional_unit_of_work_writes_after_completion()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();

        using (var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin(new UnitOfWork.Options.UnitOfWorkOptions { IsTransactional = false }))
        {
            await RecordSucceededAsync(provider, "user.created");
            Assert.Empty(Records(logs));
            await uow.CompleteAsync();
        }

        Assert.Single(Records(logs));
    }

    [Fact]
    public async Task Without_a_unit_of_work_a_succeeded_record_is_written_immediately()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();

        await RecordSucceededAsync(provider, "user.created");

        Assert.Single(Records(logs));
    }

    [Fact]
    public async Task A_failed_record_is_written_immediately_and_survives_the_rollback()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();

        using (var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IOperationRecorder>().RecordFailedAsync(
                "user.created", OperationTarget.For("u-1", "Ada"), "App.Users.Create", OperationFailure.FromCode("User:NameTaken"));

            var record = Assert.Single(Records(logs));
            Assert.Equal(LogLevel.Warning, record.Level);
            Assert.Equal(OperationRecordLogging.FailedEventId, record.Id.Id);
            Assert.Equal("User:NameTaken", Field(record, "OperationFailureCode"));

            await uow.RollbackAsync();
        }

        Assert.Single(Records(logs));
    }

    /// <summary>失败参数只记参数名：它是词条占位的原值，可能含提交者的邮箱，联系方式不进日志。</summary>
    [Fact]
    public async Task A_failed_record_logs_parameter_names_but_not_their_values()
    {
        const string email = "zhang.san@example.com";
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IOperationRecorder>().RecordFailedAsync(
            "user.created",
            OperationTarget.For("u-1", "Ada"),
            "App.Users.Create",
            OperationFailure.FromCode("User:EmailTaken", new Dictionary<string, object?> { ["Email"] = email }));

        var record = Assert.Single(Records(logs));
        Assert.Equal("""["Email"]""", Field(record, "OperationFailureDataKeys"));
        Assert.DoesNotContain(email, record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(record.StructuredState!, pair => pair.Value?.Contains(email, StringComparison.Ordinal) == true);
    }

    /// <summary>记录在调用时冻结：提交之前上下文变了（切回宿主、换了链路），写出的仍是记录当时的事实。</summary>
    [Fact]
    public async Task The_record_is_frozen_when_recorded_not_when_written()
    {
        await using var provider = Build();
        var logs = provider.GetFakeLogCollector();
        var currentTenant = provider.GetRequiredService<ICurrentTenant>();
        string? recordedId;

        using (var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            using (currentTenant.Change(TenantId))
            {
                await RecordSucceededAsync(provider, "user.created");
            }

            recordedId = null;
            await uow.CompleteAsync();
        }

        var record = Assert.Single(Records(logs));
        Assert.Equal(TenantId.ToString(), Field(record, "OperationTenantId"));
        recordedId = Field(record, "OperationRecordId");
        Assert.True(Guid.TryParse(recordedId, out _));
        Assert.False(string.IsNullOrEmpty(Field(record, "OperationTime")));
    }

    /// <summary>业务提交之后日志写不出去：不能把已提交的业务报成失败，也不能被当作失败再补记一条失败记录。</summary>
    [Fact]
    public async Task A_delivery_failure_after_commit_does_not_fail_the_unit_of_work()
    {
        var throwing = new ThrowingLoggerProvider(OperationRecordLogging.CategoryName);
        await using var provider = Build(logging => logging.AddProvider(throwing));
        var logs = provider.GetFakeLogCollector();

        using (var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            await RecordSucceededAsync(provider, "user.created");
            await uow.CompleteAsync();
        }

        var delivery = Assert.Single(logs.GetSnapshot(), r => r.Category == OperationRecordLogging.DeliveryCategoryName);
        Assert.Equal(LogLevel.Critical, delivery.Level);
        Assert.Equal(OperationRecordLogging.DeliveryFailedEventId, delivery.Id.Id);
        Assert.DoesNotContain(Records(logs), r => Field(r, "OperationOutcome") == "Failed");
    }

    [Fact]
    public void AddOperationRecordsLogging_registers_only_the_writer()
    {
        var services = new ServiceCollection();

        services.AddOperationRecordsLogging();

        services.AssertSingle<IOperationRecordWriter>(ServiceLifetime.Transient);
        services.AssertNotRegistered<IOperationRecordReader>();
        services.AssertNotRegistered<IOperationRecordQueryService>();
        services.AssertSingle<IOperationRecorder>(ServiceLifetime.Transient);
    }

    [Fact]
    public void AddOperationRecordsLogging_is_idempotent()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddOperationRecordsLogging());
    }

    [Fact]
    public void The_log_writer_and_a_storage_adapter_cannot_be_combined()
    {
        var services = new ServiceCollection();
        services.AddOperationRecordsEfCore<TestDbContext>();

        Assert.Throws<InvalidOperationException>(() => services.AddOperationRecordsLogging());

        var reversed = new ServiceCollection();
        reversed.AddOperationRecordsLogging();
        Assert.Throws<InvalidOperationException>(() => reversed.AddOperationRecordsEfCore<TestDbContext>());
    }

    /// <summary>类别被过滤掉时安全记录会静默消失，宿主启动即失败。</summary>
    [Fact]
    public async Task Startup_fails_when_the_record_category_is_filtered_out()
    {
        await using var provider = Build(logging => logging.AddFilter(OperationRecordLogging.CategoryName, LogLevel.Warning));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));
        Assert.Contains(OperationRecordLogging.CategoryName, exception.Message);
    }

    [Fact]
    public async Task Startup_fails_without_the_unit_of_work_and_local_event_bus()
    {
        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddFakeLogging())
            .AddOperationRecordsLogging();
        await using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));
        Assert.Contains("AddUnitOfWork", exception.Message);
    }

    [Fact]
    public async Task Startup_passes_with_a_complete_composition()
    {
        await using var provider = Build();

        await StartAsync(provider);
    }

    private static async Task StartAsync(IServiceProvider provider)
    {
        // 只跑本组件的启动检查：用例直接建容器，不经 Host，工作单元的织入检查不适用
        foreach (var lifecycle in provider.GetServices<IHostedService>().OfType<OperationRecordLoggingStartupCheck>())
        {
            await lifecycle.StartingAsync(CancellationToken.None);
        }
    }

    private static async Task RecordSucceededAsync(IServiceProvider provider, string action)
    {
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IOperationRecorder>().RecordSucceededAsync(
            action, OperationTarget.For("u-1", "Ada"), "App.Users.Create");
    }

    private static IReadOnlyList<FakeLogRecord> Records(FakeLogCollector logs)
        => [.. logs.GetSnapshot().Where(r => r.Category == OperationRecordLogging.CategoryName)];

    private static string? Field(FakeLogRecord record, string name)
        => record.StructuredState?.FirstOrDefault(pair => pair.Key == name).Value;

    private static ServiceProvider Build(Action<ILoggingBuilder>? configureLogging = null, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFakeLogging();
            configureLogging?.Invoke(logging);
        });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLocalEventBus();
        services.AddUnitOfWork();
        services.AddSingleton<IClock, UtcClockProvider>();
        services.AddMultiTenancyCore();
        services.AddAmbientContext();
        services.AddCorrelationIdCore();
        services.AddSingleton<IOperationActionDefinitionManager>(new FakeOperationActionDefinitionManager());
        services.AddOperationRecordsLogging();
        configureServices?.Invoke(services);
        return (ServiceProvider)new DynamicProxyServiceRegistrationCallbackFactory().CreateServiceProvider(services);
    }

    public sealed class FailingSideEffect : LocalEvent;

    public sealed class FailingSideEffectHandler : IEventHandler<FailingSideEffect>
    {
        public Task HandleAsync(FailingSideEffect @event, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("side effect unavailable"));
    }

    private sealed class FailingCommitTransactionApi : ITransactionApi
    {
        public Task CommitAsync() => Task.FromException(new InvalidOperationException("commit failed"));

        public void Dispose() { }
    }

    private sealed class ThrowingLoggerProvider(string category) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
            => categoryName == category ? new ThrowingLogger() : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => throw new IOException("log sink unavailable");
        }
    }
}
