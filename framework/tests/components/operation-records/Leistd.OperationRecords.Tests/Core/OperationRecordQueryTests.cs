using System.Globalization;
using System.Security.Claims;
using System.Text;
using Leistd.Localization.AspNetCore;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Errors;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Queries;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Dtos;
using Leistd.OperationRecords.Options;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.Security.Claims;
using Leistd.TestBase.Doubles;
using Leistd.Timing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.OperationRecords.Tests.Core;

/// <summary>查询用例：读者判定、筛选展开、字段裁剪与导出，分页与导出共用同一份判定。</summary>
/// <remarks>
/// 读者判定写错的症状是越权而不是报错：租户读者看到宿主层记录、仅宿主字段下发给租户、
/// "界面看不到的记录能被导出来"。这些都只在建了租户之后才暴露。
/// </remarks>
public sealed class OperationRecordQueryTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");


    private static readonly Dictionary<string, OperationVisibility> Definitions = new(StringComparer.Ordinal)
    {
        ["user.created"] = OperationVisibility.Tenant,
        ["tenant.created"] = OperationVisibility.Host,
        ["auth.login.succeeded"] = OperationVisibility.Tenant,
        ["operation-records.exported"] = OperationVisibility.Tenant
    };

    private static (OperationRecordQueryService Service, RecordingOperationRecordStore Store) Create(
        bool hostReader,
        string? subject = "reader-1",
        Guid? readerTenantId = null,
        IStringLocalizer? localizer = null)
    {
        var store = new RecordingOperationRecordStore();
        var definitions = new CategorizedDefinitions();
        var clock = new UtcClockProvider(new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero)));
        var currentTenant = new FakeCurrentTenant(hostReader ? null : TenantId);
        var currentUser = new FakeCurrentUser(
            id: null,
            tenantId: readerTenantId,
            claims: subject is null ? [] : [new Claim(CustomClaimTypes.Subject, subject)]);
        var options = Microsoft.Extensions.Options.Options.Create(new OperationRecordOptions());
        var recorder = new OperationRecorder(
            store, definitions, currentTenant, currentUser, new FakeCorrelationIdProvider(null), clock, options,
            new FakeLogger<OperationRecorder>(new FakeLogCollector()),
            new RecordedFailureTracker());

        return (new OperationRecordQueryService(store, definitions, recorder, currentTenant, currentUser, clock, localizer), store);
    }

    private static OperationRecordInfo Record(
        string action = "user.created",
        string? actorId = "someone",
        string? targetName = "Ada",
        OperationRecordOutcome outcome = OperationRecordOutcome.Succeeded,
        string? failureCode = null,
        string? failureData = null) => new()
        {
            Action = action,
            TargetId = "t-1",
            TargetName = targetName,
            AuthorizationBasis = "App.Users.Create",
            Outcome = outcome,
            Visibility = OperationVisibility.Tenant,
            ActorId = actorId,
            FailureCode = failureCode,
            FailureData = failureData,
            FailureDetail = "db timeout",
            CorrelationId = "trace-1",
            ActorTenantId = TenantId
        };

    [Fact]
    public async Task A_host_reader_queries_every_layer_and_sees_host_only_fields()
    {
        var (service, store) = Create(hostReader: true);
        store.Written.Add(Record());

        var page = await service.GetPagedListAsync(new GetOperationRecordPagedInputDto());

        Assert.Same(OperationRecordVisibilityScope.Host, store.LastQuery!.Value.Criteria.Scope);
        var row = Assert.Single(page.Items);
        Assert.Equal(("db timeout", "trace-1", TenantId), (row.FailureDetail, row.CorrelationId, row.ActorTenantId));
    }

    /// <summary>租户读者：范围按本人收窄，仅宿主字段裁掉。</summary>
    /// <remarks>读者标识与所属租户和记录器取操作人同一口径（claim 原始值与主体的租户 claim），机器主体因此也读得到自己的 Actor 层记录。</remarks>
    [Fact]
    public async Task A_tenant_reader_is_scoped_to_itself_and_host_only_fields_are_trimmed()
    {
        var (service, store) = Create(hostReader: false, subject: "client:reporting", readerTenantId: TenantId);
        store.Written.Add(Record());

        var page = await service.GetPagedListAsync(new GetOperationRecordPagedInputDto());

        var scope = store.LastQuery!.Value.Criteria.Scope;
        Assert.Equal(
            (true, false, "client:reporting", TenantId),
            (scope.IsRestricted, scope.IncludesHostRecords, scope.ActorId, scope.ActorTenantId));
        var row = Assert.Single(page.Items);
        Assert.Equal((null, null, null), (row.FailureDetail, row.CorrelationId, (Guid?)row.ActorTenantId));
    }

    /// <summary>类别与动作两个维度取交集；租户读者直接传宿主动作码也会被滤掉。</summary>
    [Fact]
    public async Task Categories_and_actions_intersect_within_what_the_reader_can_see()
    {
        var (service, store) = Create(hostReader: false);

        await service.GetPagedListAsync(new GetOperationRecordPagedInputDto
        {
            Categories = ["account", "tenant"],
            Actions = ["user.created", "tenant.created", "auth.login.succeeded"]
        });

        Assert.Equal(["user.created"], store.LastQuery!.Value.Criteria.Actions);
    }

    /// <summary>筛了但展开为空时返回空页，不下传。</summary>
    /// <remarks>存储把空集合当"不过滤"；下传就把"筛了但没有"显示成了"这就是全部"。</remarks>
    [Fact]
    public async Task A_filter_that_expands_to_nothing_returns_an_empty_page_without_querying()
    {
        var (service, store) = Create(hostReader: false);
        store.Written.Add(Record());

        var page = await service.GetPagedListAsync(new GetOperationRecordPagedInputDto { Actions = ["tenant.created"] });

        Assert.Empty(page.Items);
        Assert.Null(store.LastQuery);
    }

    [Fact]
    public async Task Filter_options_hide_host_actions_from_tenant_readers()
    {
        var (tenantService, _) = Create(hostReader: false);
        var (hostService, _) = Create(hostReader: true);

        var tenantOptions = await tenantService.GetFilterOptionsAsync();
        var hostOptions = await hostService.GetFilterOptionsAsync();

        Assert.DoesNotContain(tenantOptions.Actions, a => a.Code == "tenant.created");
        Assert.DoesNotContain("tenant", tenantOptions.Categories);
        Assert.Contains(hostOptions.Actions, a => a.Code == "tenant.created");
    }

    /// <summary>自证类动作成功、操作人就是目标时，目标承载"什么人"；失败的、没有操作人的或操作人另有其人的不算。</summary>
    [Theory]
    [InlineData("auth.login.succeeded", null, OperationRecordOutcome.Succeeded, false)]
    [InlineData("auth.login.succeeded", "t-1", OperationRecordOutcome.Succeeded, true)]
    [InlineData("auth.login.succeeded", null, OperationRecordOutcome.Failed, false)]
    [InlineData("auth.login.succeeded", "someone", OperationRecordOutcome.Succeeded, false)]
    [InlineData("user.created", null, OperationRecordOutcome.Succeeded, false)]
    public async Task The_target_stands_in_for_the_actor_only_for_verified_self_acting_success(
        string action, string? actorId, OperationRecordOutcome outcome, bool expected)
    {
        var (service, store) = Create(hostReader: true);
        store.Written.Add(Record(action, actorId, outcome: outcome));

        var page = await service.GetPagedListAsync(new GetOperationRecordPagedInputDto());

        Assert.Equal(expected, Assert.Single(page.Items).ActorIsTarget);
    }

    [Fact]
    public async Task An_inverted_time_range_is_rejected_with_field_errors()
    {
        var (service, _) = Create(hostReader: true);

        var error = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => service.GetPagedListAsync(
            new GetOperationRecordPagedInputDto { StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddDays(-1) }));

        Assert.Equal(["StartTime", "EndTime"], error.ValidationResult?.MemberNames);
    }

    /// <summary>未定义的结果值由用例自己按 Outcome 字段拒绝：不查存储，导出也不留导出记录。</summary>
    /// <remarks>HTTP 端点先用字符串解析器拦掉了 <c>999</c>，内部直接调用用例的消费者只能靠这一层。</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_undefined_outcome_is_rejected_as_an_outcome_error_by_the_list_and_export(bool export)
    {
        var (service, store) = Create(hostReader: true);
        const OperationRecordOutcome undefined = (OperationRecordOutcome)999;

        var error = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => export
            ? (Task)service.ExportAsync(
                new ExportOperationRecordsInputDto { Outcome = undefined },
                new OperationRecordExportAudit("operation-records.exported", "App.OperationRecords.Export"))
            : service.GetPagedListAsync(new GetOperationRecordPagedInputDto { Outcome = undefined }));

        Assert.Equal(["Outcome"], error.ValidationResult?.MemberNames);
        Assert.Null(store.LastQuery);
        Assert.DoesNotContain(store.Written, record => record.Action == "operation-records.exported");
    }

    /// <summary>导出：带 BOM、用户可控字段防公式注入、仅宿主列只在宿主导出时成列，文件生成后才记导出。</summary>
    /// <remarks>
    /// 目标名是用户可控内容：显示名改成 <c>=cmd|...</c> 的人能让打开审计文件的管理员执行命令。
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_export_is_safe_to_open_and_audited(bool hostReader)
    {
        var (service, store) = Create(hostReader);
        store.Written.Add(Record(targetName: "=cmd|' /C calc'!A0"));

        var file = await service.ExportAsync(
            new ExportOperationRecordsInputDto(),
            new OperationRecordExportAudit("operation-records.exported", "App.OperationRecords.Export"));

        var bom = Encoding.UTF8.GetPreamble();
        Assert.Equal(bom, file.Content.Take(bom.Length));
        var text = Encoding.UTF8.GetString(file.Content, bom.Length, file.Content.Length - bom.Length);
        Assert.Contains("'=cmd", text);
        Assert.Equal(hostReader, text.Contains("CorrelationId", StringComparison.Ordinal));
        Assert.Equal(("text/csv", "operation-records-20260920100000.csv"), (file.ContentType, file.FileName));

        var audit = store.Written[^1];
        Assert.Equal(("operation-records.exported", "App.OperationRecords.Export"), (audit.Action, audit.AuthorizationBasis));
    }

    private static readonly CultureLocalizer EmailTakenLocalizer = new(new Dictionary<string, Dictionary<string, string>>
    {
        ["en"] = new() { ["User:EmailTaken"] = "Email '{Email}' is already in use ({Attempts} attempts)." },
        ["zh-CN"] = new() { ["User:EmailTaken"] = "邮箱 '{Email}' 已被使用（{Attempts} 次）。" }
    });

    /// <summary>失败原因按码查文案、用参数填具名占位符；数值参数按字面量填入。</summary>
    [Fact]
    public async Task The_failure_message_is_the_localized_text_filled_with_the_recorded_parameters()
    {
        var (service, store) = Create(hostReader: true, localizer: EmailTakenLocalizer);
        store.Written.Add(Record(
            outcome: OperationRecordOutcome.Failed,
            failureCode: "User:EmailTaken",
            failureData: """{"Email":"a@b.com","Attempts":3}"""));

        var row = await InCulture("en", async () => Assert.Single((await service.GetPagedListAsync(new GetOperationRecordPagedInputDto())).Items));

        Assert.Equal("Email 'a@b.com' is already in use (3 attempts).", row.FailureMessage);
    }

    /// <summary>同一条记录按读取时的请求语言渲染，库里不存句子。</summary>
    [Fact]
    public async Task Readers_in_different_languages_get_the_failure_message_in_their_own_language()
    {
        var (service, store) = Create(hostReader: true, localizer: EmailTakenLocalizer);
        store.Written.Add(Record(
            outcome: OperationRecordOutcome.Failed,
            failureCode: "User:EmailTaken",
            failureData: """{"Email":"a@b.com","Attempts":3}"""));

        var english = await InCulture("en", () => ReadFailureMessageAsync(service));
        var chinese = await InCulture("zh-CN", () => ReadFailureMessageAsync(service));

        Assert.Equal(
            ("Email 'a@b.com' is already in use (3 attempts).", "邮箱 'a@b.com' 已被使用（3 次）。"),
            (english, chinese));
    }

    /// <summary>取不到文案时为空，由调用方回落：没有本地化器、词条缺失、没有失败码。</summary>
    [Theory]
    [InlineData(false, "User:EmailTaken")]
    [InlineData(true, "Order:Unknown")]
    [InlineData(true, null)]
    public async Task The_failure_message_is_null_when_no_text_can_be_found(bool withLocalizer, string? failureCode)
    {
        var (service, store) = Create(hostReader: true, localizer: withLocalizer ? EmailTakenLocalizer : null);
        store.Written.Add(Record(outcome: OperationRecordOutcome.Failed, failureCode: failureCode));

        Assert.Null(await InCulture("en", () => ReadFailureMessageAsync(service)));
    }

    /// <summary>参数 JSON 坏了按无参数渲染：一条坏记录不能拖垮整页，占位符原样保留。</summary>
    [Theory]
    [InlineData("{not json")]
    [InlineData("""["a@b.com"]""")]
    public async Task Unreadable_failure_data_renders_the_text_without_parameters(string failureData)
    {
        var (service, store) = Create(hostReader: true, localizer: EmailTakenLocalizer);
        store.Written.Add(Record(outcome: OperationRecordOutcome.Failed, failureCode: "User:EmailTaken", failureData: failureData));

        Assert.Equal(
            "Email '{Email}' is already in use ({Attempts} attempts).",
            await InCulture("en", () => ReadFailureMessageAsync(service)));
    }

    /// <summary>导出与列表同一口径：码与参数两列之外，另有按导出请求语言渲染的原因列。</summary>
    [Fact]
    public async Task An_export_carries_the_failure_message_in_the_exporting_language()
    {
        var (service, store) = Create(hostReader: false, localizer: EmailTakenLocalizer);
        store.Written.Add(Record(
            outcome: OperationRecordOutcome.Failed,
            failureCode: "User:EmailTaken",
            failureData: """{"Email":"a@b.com","Attempts":3}"""));

        var file = await InCulture("zh-CN", () => service.ExportAsync(
            new ExportOperationRecordsInputDto(),
            new OperationRecordExportAudit("operation-records.exported", "App.OperationRecords.Export")));

        var lines = Encoding.UTF8.GetString(file.Content).TrimStart('\uFEFF').Split(Environment.NewLine);
        Assert.EndsWith("FailureCode,FailureData,FailureMessage", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("邮箱 'a@b.com' 已被使用（3 次）。", lines[1], StringComparison.Ordinal);
    }

    /// <summary>审计专用的 <c>{码}:Record</c> 优先于码本身，二者都用记录的参数填占位符。</summary>
    /// <remarks>
    /// 登录失败这类记录带着接口报错没有的参数（次数、时长），措辞只能另备一条。
    /// </remarks>
    [Fact]
    public async Task The_record_specific_text_wins_over_the_error_text()
    {
        var localizer = new CultureLocalizer(new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new()
            {
                ["Auth:InvalidCredentials"] = "The username or password is incorrect.",
                ["Auth:InvalidCredentials:Record"] = "Incorrect username or password ({attempts} failed attempts)"
            }
        });
        var (service, store) = Create(hostReader: true, localizer: localizer);
        store.Written.Add(Record(
            outcome: OperationRecordOutcome.Failed,
            failureCode: "Auth:InvalidCredentials",
            failureData: """{"attempts":5}"""));

        Assert.Equal(
            "Incorrect username or password (5 failed attempts)",
            await InCulture("en", () => ReadFailureMessageAsync(service)));
    }

    /// <summary>
    /// 审计键按本地化器的完整回落链查：请求语言缺审计键而默认语言有，用的是默认语言的审计措辞，
    /// 而不是请求语言的普通文案
    /// </summary>
    /// <remarks>
    /// 查找只分两步（先审计键、再码本身），每一步都交给本地化器自己回落；
    /// 替身与 JsonStringLocalizer 同样按"当前文化 → 父文化 → 默认语言"回落，用例才反映真实行为。
    /// </remarks>
    [Fact]
    public async Task A_record_specific_text_anywhere_on_the_fallback_chain_wins_over_the_error_text()
    {
        var localizer = new CultureLocalizer(new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new() { ["User:EmailTaken:Record"] = "Record text {Email}" },
            ["zh-CN"] = new() { ["User:EmailTaken"] = "邮箱 '{Email}' 已被使用。" }
        });
        var (service, store) = Create(hostReader: true, localizer: localizer);
        store.Written.Add(Record(
            outcome: OperationRecordOutcome.Failed,
            failureCode: "User:EmailTaken",
            failureData: """{"Email":"a@b.com"}"""));

        Assert.Equal("Record text a@b.com", await InCulture("zh-CN", () => ReadFailureMessageAsync(service)));
    }

    /// <summary>审计键在整条回落链上都没有，才用码本身的文案（同样按请求语言回落）。</summary>
    [Fact]
    public async Task Without_a_record_specific_text_on_the_fallback_chain_the_error_text_is_used()
    {
        var localizer = new CultureLocalizer(new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new() { ["User:EmailTaken"] = "Email '{Email}' is already in use." },
            ["zh-CN"] = new() { ["User:EmailTaken"] = "邮箱 '{Email}' 已被使用。" }
        });
        var (service, store) = Create(hostReader: true, localizer: localizer);
        store.Written.Add(Record(
            outcome: OperationRecordOutcome.Failed,
            failureCode: "User:EmailTaken",
            failureData: """{"Email":"a@b.com"}"""));

        Assert.Equal("邮箱 'a@b.com' 已被使用。", await InCulture("zh-CN", () => ReadFailureMessageAsync(service)));
    }

    /// <summary>组件自产的失败码由组件自带译文：宿主只注册本地化与本组件、资源里没有这条词条时，原因照样渲染成句子。</summary>
    [Theory]
    [InlineData("en", "Not allowed to perform this action.")]
    [InlineData("zh-CN", "没有执行该操作的权限。")]
    public async Task The_component_failure_code_renders_from_the_bundled_resources(string culture, string expected)
    {
        using var provider = new ServiceCollection()
            .AddJsonLocalization()
            .AddOperationRecords()
            .BuildServiceProvider();
        var (service, store) = Create(hostReader: true, localizer: provider.GetRequiredService<IStringLocalizer>());
        store.Written.Add(Record(outcome: OperationRecordOutcome.Failed, failureCode: OperationFailureCodes.Forbidden));

        Assert.Equal(expected, await InCulture(culture, () => ReadFailureMessageAsync(service)));
    }

    private static async Task<string?> ReadFailureMessageAsync(OperationRecordQueryService service)
        => Assert.Single((await service.GetPagedListAsync(new GetOperationRecordPagedInputDto())).Items).FailureMessage;

    // 本地化器按查表那一刻的 CurrentUICulture 取文案，与请求本地化中间件设置的口径相同
    private static async Task<T> InCulture<T>(string culture, Func<Task<T>> action)
    {
        var original = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            return await action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    // 与 JsonStringLocalizer 同一回落口径：当前 UI 文化 → 各级父文化 → 默认语言（en），都没有才算未命中
    private sealed class CultureLocalizer(Dictionary<string, Dictionary<string, string>> texts) : IStringLocalizer
    {
        private const string DefaultCulture = "en";

        public LocalizedString this[string name]
        {
            get
            {
                foreach (var culture in CultureChain())
                {
                    if (texts.TryGetValue(culture, out var entries) && entries.TryGetValue(name, out var value))
                    {
                        return new LocalizedString(name, value);
                    }
                }

                return new LocalizedString(name, name, resourceNotFound: true);
            }
        }

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];

        private static IEnumerable<string> CultureChain()
        {
            for (var culture = CultureInfo.CurrentUICulture;
                 !culture.Equals(CultureInfo.InvariantCulture);
                 culture = culture.Parent)
            {
                yield return culture.Name;
            }

            yield return DefaultCulture;
        }
    }

    // 类别按动作码前缀划分，只为让用例能表达"按类别展开"
    private sealed class CategorizedDefinitions : IOperationActionDefinitionManager
    {
        private readonly List<IOperationActionDefinition> _all =
        [
            .. Definitions.Select(pair => (IOperationActionDefinition)new FakeOperationActionDefinition(
                pair.Key,
                pair.Value,
                category: pair.Key.StartsWith("user.", StringComparison.Ordinal) ? "account"
                    : pair.Key.StartsWith("tenant.", StringComparison.Ordinal) ? "tenant"
                    : pair.Key.StartsWith("auth.", StringComparison.Ordinal) ? "authentication" : "data",
                targetIsActor: pair.Key == "auth.login.succeeded"))
        ];

        public IOperationActionDefinition? GetOrNull(string code) => _all.FirstOrDefault(d => d.Code == code);
        public IReadOnlyList<IOperationActionDefinition> GetAll() => _all;
        public IReadOnlyList<string> GetCategories() => [.. _all.Select(d => d.Category).Distinct()];
    }
}
