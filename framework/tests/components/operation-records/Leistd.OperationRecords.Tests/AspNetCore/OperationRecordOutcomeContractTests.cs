using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.AspNetCore.Endpoints;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.EntityFrameworkCore;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.Security.Users;
using Leistd.TestBase.Doubles;
using Leistd.Timing;
using Leistd.Tracing.Abstractions;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.OperationRecords.Tests.AspNetCore;

/// <summary>结果与严重度的公开写法：经正式端点、真实查询用例与 SQLite 存储，输入输出都是 camelCase 名称。</summary>
/// <remarks>
/// 宿主配置与模板相同的 JSON 枚举转换器（camelCase、拒绝整数）。查询串不经这个转换器：
/// 直接绑定可空枚举会拒绝 <c>succeeded</c>、放过 <c>0</c> 与 <c>999</c>，所以列表与导出两条端点都要验。
/// </remarks>
public sealed class OperationRecordOutcomeContractTests : IAsyncLifetime
{
    private const string Policy = "records";
    private const string ExportAction = "operation-records.exported";
    private readonly string _connectionString = $"Data Source=records-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection _anchor = default!;
    private IHost _host = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        // 共享缓存的内存库在最后一个连接关闭时消失，锚连接让它活到用例结束
        _anchor = new SqliteConnection(_connectionString);
        await _anchor.OpenAsync();

        _host = await new HostBuilder()
            .UseServiceProviderFactory(new DynamicProxyServiceRegistrationCallbackFactory())
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant(null));
                    services.AddSingleton<ICurrentUser>(new FakeCurrentUser());
                    services.AddSingleton<ICorrelationIdProvider>(new FakeCorrelationIdProvider(null));
                    services.AddSingleton<IClock>(new UtcClockProvider(new FakeTimeProvider()));
                    services.AddSingleton<IOperationActionDefinitionManager>(new FakeOperationActionDefinitionManager(
                        new Dictionary<string, OperationVisibility>
                        {
                            ["user.created"] = OperationVisibility.Tenant,
                            [ExportAction] = OperationVisibility.Tenant
                        }));
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddUnitOfWork();
                    services.AddUnitOfWorkEfCore();
                    services.AddDbContext<TestDbContext>(options => options.UseSqlite(_connectionString));
                    services.AddOperationRecordsEfCore<TestDbContext>();
                    services.AddGlobalExceptionHandler();
                    services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(
                        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
                    // 本组只验证取值写法，授权口径由 OperationRecordEndpointTests 钉住
                    services.AddAuthorization(options => options.AddPolicy(Policy, policy => policy.RequireAssertion(_ => true)));
                })
                .Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapGroup("/records").MapOperationRecords(options =>
                    {
                        options.ReadPolicy = Policy;
                        options.ExportPolicy = Policy;
                        options.ExportAction = ExportAction;
                    }));
                }))
            .StartAsync();

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            await services.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
            var writer = services.GetRequiredService<IOperationRecordWriter>();
            await writer.InsertAsync(Record("u-1", OperationRecordOutcome.Succeeded));
            await writer.InsertAsync(Record("u-2", OperationRecordOutcome.Failed));
        }

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        await _anchor.DisposeAsync();
    }

    [Theory]
    [InlineData("succeeded", new[] { "u-1" })]
    [InlineData("failed", new[] { "u-2" })]
    [InlineData(null, new[] { "u-1", "u-2" })]
    public async Task The_list_filters_by_the_camel_case_outcome_and_returns_it_in_camel_case(string? outcome, string[] targets)
    {
        var response = await _client.GetAsync("/records" + (outcome is null ? string.Empty : $"?outcome={outcome}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (Target: item.GetProperty("targetId").GetString()!, Outcome: item.GetProperty("outcome").GetString()!))
            .OrderBy(item => item.Target, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(targets, items.Select(item => item.Target));
        Assert.All(items, item => Assert.Equal(item.Target == "u-1" ? "succeeded" : "failed", item.Outcome));
    }

    [Theory]
    [InlineData("failed", new[] { "failed" })]
    [InlineData(null, new[] { "failed", "succeeded" })]
    public async Task The_export_filters_by_the_camel_case_outcome_and_writes_it_in_camel_case(string? outcome, string[] outcomes)
    {
        var response = await _client.GetAsync("/records/export" + (outcome is null ? string.Empty : $"?outcome={outcome}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()).TrimStart('﻿')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("Outcome", lines[0].Split(',')[2]);
        Assert.Equal(outcomes, lines.Skip(1).Select(line => line.Split(',')[2]).Order(StringComparer.Ordinal));
    }

    /// <summary>只认公开名称：成员名的原样写法、数字与未定义值都按字段 <c>outcome</c> 拒绝，导出也不例外。</summary>
    [Theory]
    [InlineData("/records", "Succeeded")]
    [InlineData("/records", "FAILED")]
    [InlineData("/records", "0")]
    [InlineData("/records", "1")]
    [InlineData("/records", "999")]
    [InlineData("/records", "unknown")]
    [InlineData("/records/export", "Failed")]
    [InlineData("/records/export", "1")]
    [InlineData("/records/export", "999")]
    public async Task Other_spellings_numbers_and_undefined_values_are_rejected_as_an_outcome_error(string path, string outcome)
    {
        var response = await _client.GetAsync($"{path}?outcome={outcome}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["outcome"], document.RootElement.GetProperty("errors").EnumerateArray()
            .Select(error => error.GetProperty("field").GetString()));
    }

    [Fact]
    public async Task Filter_options_return_the_severity_in_camel_case()
    {
        var response = await _client.GetAsync("/records/filter-options");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            [(ExportAction, "info"), ("user.created", "info")],
            document.RootElement.GetProperty("actions").EnumerateArray()
                .Select(action => (action.GetProperty("code").GetString(), action.GetProperty("severity").GetString()))
                .OrderBy(action => action.Item1, StringComparer.Ordinal));
    }

    private static OperationRecordInfo Record(string targetId, OperationRecordOutcome outcome) => new()
    {
        Action = "user.created",
        TargetId = targetId,
        AuthorizationBasis = "App.Users.Create",
        Outcome = outcome,
        Visibility = OperationVisibility.Tenant
    };
}
