using Leistd.BackgroundJobs.EntityFrameworkCore;
using Leistd.BackgroundJobs.EntityFrameworkCore.Stores;
using Leistd.BackgroundJobs.InProcess;
using Leistd.BackgroundJobs.InProcess.Options;
using Leistd.BackgroundJobs.InProcess.Recurring;
using Leistd.BackgroundJobs.Queues;
using Leistd.BackgroundJobs.Recurring;
using Leistd.BackgroundJobs.Tests.TestDoubles;
using Leistd.TestBase.Assertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>登记面：任务名全局唯一、范围必须显式、调度器与水位存储的注册与调用顺序无关。</summary>
public class RecurringJobRegistrationTests
{
    private static readonly RecurringJobSchedule Hourly = RecurringJobSchedule.Every(TimeSpan.FromHours(1));

    [Fact]
    public void A_job_is_registered_as_a_definition_and_a_transient_service()
    {
        var services = new ServiceCollection().AddRecurringJob<FailingJob>("test.failing", Hourly, RecurringJobScope.Cluster);

        var definition = Assert.Single(services.Select(d => d.ImplementationInstance).OfType<RecurringJobDefinition>());
        Assert.Equal(("test.failing", typeof(FailingJob), RecurringJobScope.Cluster), (definition.Name, definition.JobType, definition.Scope));
        services.AssertSingle<FailingJob>(ServiceLifetime.Transient);
    }

    [Fact]
    public void Registering_the_same_job_twice_is_idempotent()
    {
        ServiceCollectionAssertions.AssertIdempotent(
            services => services.AddRecurringJob<FailingJob>("test.failing", Hourly, RecurringJobScope.Cluster));
    }

    /// <summary>名字同时是集群锁与水位的键：两个任务共用一个名字会互相跳过，而且不报错。</summary>
    [Fact]
    public void Two_jobs_cannot_share_a_name()
    {
        var services = new ServiceCollection().AddRecurringJob<FailingJob>("test.shared", Hourly, RecurringJobScope.Cluster);

        Assert.Throws<InvalidOperationException>(
            () => services.AddRecurringJob<CountingJob>("test.shared", Hourly, RecurringJobScope.Cluster));
    }

    /// <summary>范围没有默认值：<c>default</c> 不是合法取值，漏选在登记时就失败。</summary>
    [Fact]
    public void The_scope_must_be_chosen_explicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceCollection().AddRecurringJob<FailingJob>("test.failing", Hourly, default));
    }

    [Fact]
    public void The_in_process_implementation_registers_one_scheduler_and_one_queue()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddInProcessBackgroundJobs());

        var services = new ServiceCollection().AddInProcessBackgroundJobs().AddInProcessBackgroundJobs();
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RecurringJobScheduler));
        services.AssertSingle<IBackgroundTaskQueue>(ServiceLifetime.Singleton);
    }

    /// <summary>状态读取是可替换的单实现：重复调用只留一条，宿主先登记的不被覆盖，之后 Replace 也能换掉。</summary>
    [Fact]
    public void The_status_reader_is_a_replaceable_singleton()
    {
        var services = new ServiceCollection().AddInProcessBackgroundJobs().AddInProcessBackgroundJobs();
        services.AssertSingle<IRecurringJobStatusReader>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<IRecurringJobStatusReader, RecurringJobStatusReader>();

        var hostFirst = new ServiceCollection().AddSingleton<IRecurringJobStatusReader, HostStatusReader>().AddInProcessBackgroundJobs();
        hostFirst.AssertSingle<IRecurringJobStatusReader>(ServiceLifetime.Singleton);
        hostFirst.AssertImplementedBy<IRecurringJobStatusReader, HostStatusReader>();

        var replaced = new ServiceCollection().AddInProcessBackgroundJobs()
            .Replace(ServiceDescriptor.Singleton<IRecurringJobStatusReader, HostStatusReader>());
        replaced.AssertSingle<IRecurringJobStatusReader>(ServiceLifetime.Singleton);
        replaced.AssertImplementedBy<IRecurringJobStatusReader, HostStatusReader>();
    }

    /// <summary>EF 水位存储总是替换进程内默认实现，与调度器注册的先后无关。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_ef_state_store_wins_regardless_of_order(bool efFirst)
    {
        var services = new ServiceCollection();
        if (efFirst)
        {
            services.AddBackgroundJobsEfCore<DbContext>();
        }

        services.AddInProcessBackgroundJobs();
        if (!efFirst)
        {
            services.AddBackgroundJobsEfCore<DbContext>();
        }

        services.AssertImplementedBy<IRecurringJobStateStore, EfCoreRecurringJobStateStore<DbContext>>();
        services.AssertSingle<IRecurringJobStateStore>(ServiceLifetime.Transient);
    }

    [Fact]
    public void Registering_the_same_ef_state_store_twice_is_idempotent()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddBackgroundJobsEfCore<DbContext>());
    }

    /// <summary>两个上下文各注册一份水位时，按顺序静默取一条，另一个库里的水位永远不被读到。</summary>
    [Fact]
    public void A_second_context_for_the_state_store_is_rejected()
    {
        var services = new ServiceCollection().AddBackgroundJobsEfCore<DbContext>();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddBackgroundJobsEfCore<OtherDbContext>());

        Assert.Contains("single shared store", error.Message, StringComparison.Ordinal);
    }

    /// <summary>只替换进程内兜底：宿主自己的共享存储不是兜底，被静默移除就等于换掉了宿主的选择。</summary>
    [Fact]
    public void A_host_state_store_is_not_silently_replaced()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRecurringJobStateStore, HostStateStore>();

        Assert.Throws<InvalidOperationException>(() => services.AddBackgroundJobsEfCore<DbContext>());
    }

    [Fact]
    public void Validation_failures_name_the_configured_section()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:InProcess:QueueCapacity"] = "0" })
                .Build())
            .AddInProcessBackgroundJobs(configSectionPath: "Jobs")
            .BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<InProcessBackgroundJobOptions>>().Value);

        Assert.StartsWith("Jobs:InProcess:QueueCapacity", Assert.Single(failure.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_registration_with_another_section_is_rejected()
    {
        var services = new ServiceCollection().AddInProcessBackgroundJobs();

        Assert.Throws<InvalidOperationException>(() => services.AddInProcessBackgroundJobs(configSectionPath: "Jobs"));
    }

    private sealed class OtherDbContext(DbContextOptions<OtherDbContext> options) : DbContext(options);

    private sealed class HostStatusReader : IRecurringJobStatusReader
    {
        public Task<IReadOnlyList<RecurringJobStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecurringJobStatus>>([]);
    }

    private sealed class HostStateStore : IRecurringJobStateStore
    {
        public Task<DateTimeOffset?> GetLastCompletedSlotAsync(string jobName, CancellationToken cancellationToken = default)
            => Task.FromResult<DateTimeOffset?>(null);

        public Task SetLastCompletedSlotAsync(string jobName, DateTimeOffset slot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
