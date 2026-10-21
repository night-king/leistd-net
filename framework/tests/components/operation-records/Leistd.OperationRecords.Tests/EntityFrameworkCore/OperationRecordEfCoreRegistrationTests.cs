using Leistd.MultiTenancy;
using Leistd.OperationRecords.EntityFrameworkCore;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.EntityFrameworkCore.Stores;
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
using Leistd.UnitOfWork.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.OperationRecords.Tests.EntityFrameworkCore;

/// <summary><c>AddOperationRecordsEfCore</c> 的注册面与存储映射。</summary>
/// <remarks>
/// Leistd 靠宿主显式调用 <c>AddXxx()</c> 组合，注册结果就是公共契约的一部分：
/// 生命周期写错、重复注册、组件之间意外互相覆盖，编译期一个都发现不了。
/// </remarks>
public sealed class OperationRecordEfCoreRegistrationTests
{
    [Fact]
    public void AddOperationRecordsEfCore_registers_the_store_as_transient()
    {
        var services = new ServiceCollection();

        services.AddOperationRecordsEfCore<TestDbContext>();

        services.AssertSingle<IOperationRecordWriter>(ServiceLifetime.Transient);
        services.AssertImplementedBy<IOperationRecordWriter, EfCoreOperationRecordStore<TestDbContext>>();
        services.AssertSingle<IOperationRecordReader>(ServiceLifetime.Transient);
        services.AssertImplementedBy<IOperationRecordReader, EfCoreOperationRecordStore<TestDbContext>>();
        services.AssertSingle<IOperationRecordQueryService>(ServiceLifetime.Transient);
    }

    /// <summary>EF 入口内部已调用 Core 入口，宿主不必也调一次。</summary>
    [Fact]
    public void AddOperationRecordsEfCore_also_brings_the_recorder()
    {
        var services = new ServiceCollection();

        services.AddOperationRecordsEfCore<TestDbContext>();

        services.AssertSingle<IOperationRecorder>(ServiceLifetime.Transient);
    }

    [Fact]
    public void AddOperationRecordsEfCore_is_idempotent()
    {
        ServiceCollectionAssertions.AssertIdempotent(
            services => services.AddOperationRecordsEfCore<TestDbContext>());
    }

    /// <summary>记录器解析得出来，要宿主先组合四个独立组件。</summary>
    /// <remarks>
    /// <see cref="IOperationRecorder"/> 要回答"谁、在哪个租户、哪条链路、什么时间"，四样分别来自
    /// <c>AddAmbientContext()</c>、<c>AddMultiTenancyCore()</c>、<c>AddCorrelationIdCore()</c>
    /// 与宿主注册的 <see cref="IClock"/>。本组件一个都不替调用方注册，因此"注册面齐不齐"
    /// 编译期看不出来，只在首次解析时失败。
    /// </remarks>
    [Fact]
    public void The_recorder_resolves_once_its_cross_component_prerequisites_are_registered()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddUnitOfWork()
            .AddUnitOfWorkEfCore()
            .AddDbContext<TestDbContext>(options => options.UseSqlite("DataSource=:memory:"))
            .AddSingleton<IClock, UtcClockProvider>()
            .AddMultiTenancyCore()
            .AddAmbientContext()
            .AddCorrelationIdCore()
            .AddOperationRecordsEfCore<TestDbContext>()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOperationRecorder>());
    }

    /// <summary>第二个 DbContext 在注册期就被拒。</summary>
    /// <remarks>
    /// 静默取最后一条会让一半的审计写进宿主没预期的库——审计缺了一半比没有审计更危险，
    /// 因为查不到记录时人会以为"这件事没发生过"。
    /// </remarks>
    [Fact]
    public void A_second_authoritative_store_is_rejected_at_registration_time()
    {
        var services = new ServiceCollection();
        services.AddOperationRecordsEfCore<TestDbContext>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddOperationRecordsEfCore<SecondDbContext>());

        Assert.Contains("single authoritative store", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>可见性列有库默认值，但 EF 每次都显式写它。</summary>
    /// <remarks>
    /// 库默认值是给"宿主给存量表加这一列"的回填用的：没有它，历史行落空串、读取时枚举转换失败。
    /// 但 HasDefaultValue 会让 EF 在属性等于 CLR 默认值时省略该列，而 OperationVisibility.Tenant 正好是 0——
    /// 不配 ValueGeneratedNever，租户可见的记录会被库默认值静默写成宿主可见，那是越权。
    /// </remarks>
    [Fact]
    public void The_visibility_column_has_a_store_default_but_is_always_written()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var dbContext = new TestDbContext(options);

        var property = dbContext.Model
            .FindEntityType(typeof(OperationRecord))!
            .FindProperty(nameof(OperationRecord.Visibility))!;

        Assert.Equal(OperationVisibility.Host, property.GetDefaultValue());
        Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
    }
}
