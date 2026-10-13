using Leistd.TestBase.Doubles;
using Leistd.Data.Paging;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.EntityFrameworkCore;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.Tracing.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Leistd.OperationRecords.Tests.TestDoubles;

/// <summary>一条最小的动作定义，只承载断言要用到的字段。</summary>
internal sealed class FakeOperationActionDefinition(
    string code,
    OperationVisibility visibility,
    string category = "test",
    OperationSeverity severity = OperationSeverity.Info,
    bool targetIsActor = false) : IOperationActionDefinition
{
    public string Code { get; } = code;
    public string Category { get; } = category;
    public OperationVisibility Visibility { get; } = visibility;
    public OperationSeverity Severity { get; } = severity;
    public bool TargetIsActor { get; } = targetIsActor;
}

/// <summary>按给定的"动作码 → 可见性"表作答；表里没有的码按 <paramref name="otherCodes"/> 作答。</summary>
/// <remarks>
/// 默认把任何码都当作已登记的 <see cref="OperationVisibility.Tenant"/>，不关心可见性的用例因此不必逐个登记；
/// 验证"未登记"的用例显式传 <c>otherCodes: null</c>。
/// </remarks>
internal sealed class FakeOperationActionDefinitionManager(
    IReadOnlyDictionary<string, OperationVisibility>? registered = null,
    OperationVisibility? otherCodes = OperationVisibility.Tenant,
    IReadOnlySet<string>? selfProvingCodes = null) : IOperationActionDefinitionManager
{
    private readonly IReadOnlyDictionary<string, OperationVisibility> _registered =
        registered ?? new Dictionary<string, OperationVisibility>(StringComparer.Ordinal);

    public IOperationActionDefinition? GetOrNull(string code)
        => _registered.TryGetValue(code, out var visibility)
            ? new FakeOperationActionDefinition(code, visibility, targetIsActor: selfProvingCodes?.Contains(code) == true)
            : otherCodes is { } fallback ? new FakeOperationActionDefinition(code, fallback) : null;

    public IReadOnlyList<IOperationActionDefinition> GetAll()
        => [.. _registered.Select(pair => new FakeOperationActionDefinition(pair.Key, pair.Value))];

    public IReadOnlyList<string> GetCategories() => _registered.Count == 0 ? [] : ["test"];
}

/// <summary>固定链路标识。</summary>
internal sealed class FakeCorrelationIdProvider(string? correlationId) : ICorrelationIdProvider
{
    public string? Get() => correlationId;
    public string Create() => throw new NotSupportedException();
    public IDisposable Change(string correlationId) => throw new NotSupportedException();
}

/// <summary>把写入原样收下的存储，供断言"记了什么"。</summary>
internal sealed class RecordingOperationRecordStore : IOperationRecordWriter, IOperationRecordReader
{
    public List<OperationRecordInfo> Written { get; } = [];

    /// <summary>最近一次查询收到的查询条件与分页。</summary>
    public (OperationRecordCriteria Criteria, PageRequest Page)? LastQuery { get; private set; }

    public Task InsertAsync(OperationRecordInfo record, CancellationToken cancellationToken = default)
    {
        Written.Add(record);
        return Task.CompletedTask;
    }

    // 不做筛选：查询用例的断言关心"交给存储的条件"与"拿回来之后怎么裁剪"，筛选语义由存储自己的用例钉住
    public Task<PagedResult<OperationRecordInfo>> GetPagedListAsync(
        OperationRecordCriteria criteria,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        LastQuery = (criteria, page);
        return Task.FromResult(new PagedResult<OperationRecordInfo>(Written.Count, Written.Skip(page.Offset).Take(page.Limit)));
    }

    public Task<IReadOnlyList<int>> CountByBucketsAsync(
        OperationRecordVisibilityScope scope,
        IReadOnlyList<DateTime> edges,
        IReadOnlyCollection<string>? actions = null,
        bool distinctTargets = false,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>写入必定失败的存储，用于区分"吞掉"与"上抛"两条策略。</summary>
internal sealed class ThrowingOperationRecordStore(Exception failure) : IOperationRecordWriter
{
    public Task InsertAsync(OperationRecordInfo record, CancellationToken cancellationToken = default)
        => throw failure;
}

/// <summary>承载操作记录表的测试上下文。</summary>
/// <remarks>表名跟随本 <c>DbSet</c> 属性名，用于钉住"组件不写死表名"。</remarks>
internal sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<OperationRecord> OperationRecords => Set<OperationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ConfigureOperationRecords();
}

/// <summary>映射同一张表的第二个上下文，用于验证"只能有一个权威存储"。</summary>
internal sealed class SecondDbContext(DbContextOptions<SecondDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ConfigureOperationRecords();
}

/// <summary>把失败调用原样转成一条记录，避免 HttpContext 扩展的用例依赖记录器的上下文补齐逻辑。</summary>
// 写出之后登记去重标记，与真实 OperationRecorder 同构。
// 真实记录器自己的登记时机由 FailedOperationRecordingTests 里走真实 DI 的用例钉住，
// 这个替身只服务于"扩展拿到已登记状态之后怎么做"。
internal sealed class PassThroughRecorder(IOperationRecordWriter store, RecordedFailureTracker recordedFailures)
    : IOperationRecorder
{
    public Task RecordSucceededAsync(string action, OperationTarget target, string basis, CancellationToken ct = default)
        => throw new NotSupportedException();

    public async Task RecordFailedAsync(
        string action,
        OperationTarget target,
        string basis,
        OperationFailure failure = default)
    {
        await store.InsertAsync(new OperationRecordInfo
        {
            Action = action,
            TargetId = target.Id,
            TargetName = target.Name,
            AuthorizationBasis = basis,
            Outcome = OperationRecordOutcome.Failed,
            Visibility = OperationVisibility.Tenant,
            FailureCode = failure.Code,
            FailureData = failure.Data,
            FailureDetail = failure.Detail
        });

        recordedFailures.MarkRecorded(action, target.Id);
    }
}
