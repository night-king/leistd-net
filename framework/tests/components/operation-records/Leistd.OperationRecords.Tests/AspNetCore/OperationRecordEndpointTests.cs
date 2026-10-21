using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.Data.Paging;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Queries;
using Leistd.OperationRecords.AspNetCore.Endpoints;
using Leistd.OperationRecords.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.OperationRecords.Tests.AspNetCore;

/// <summary>端点：授权口径必填、按策略把守、查询参数与控制器形态一致。</summary>
/// <remarks>
/// 参数绑定写错的症状是前端请求整批 400 或筛选条件被静默丢掉；授权口径缺省的症状是审计数据对任何登录用户开放。
/// </remarks>
public sealed class OperationRecordEndpointTests : IAsyncLifetime
{
    private readonly CapturingQueryService _service = new();
    private IHost _host = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IOperationRecordQueryService>(_service);
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("Test", _ => { });
                    services.AddAuthorization(options =>
                    {
                        options.AddPolicy("records.read", p => p.RequireClaim("permission", "read"));
                        options.AddPolicy("records.export", p => p.RequireClaim("permission", "export"));
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapGroup("/api/v1/operation-records").MapOperationRecords(o =>
                    {
                        o.ReadPolicy = "records.read";
                        o.ExportPolicy = "records.export";
                        o.ExportAction = "operation-records.exported";
                    }));
                }))
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>只写日志的模式没有历史可读：映射端点当场报错，而不是第一次请求才 500。</summary>
    [Fact]
    public async Task Mapping_without_a_history_store_fails_at_startup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        await using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => app.MapGroup("/records").MapOperationRecords(o =>
        {
            o.ReadPolicy = "records.read";
            o.ExportPolicy = "records.export";
            o.ExportAction = "operation-records.exported";
        }));
        Assert.Contains("history store", exception.Message);
    }

    private Task<HttpResponseMessage> GetAsync(string url, string? permission)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (permission is not null)
        {
            request.Headers.Add("X-Permission", permission);
        }

        return _client.SendAsync(request);
    }

    [Fact]
    public void Mapping_without_the_authorization_settings_fails()
    {
        var app = WebApplication.CreateBuilder().Build();

        Assert.Throws<ArgumentException>(() => app.MapOperationRecords(o => o.ReadPolicy = "records.read"));
    }

    [Theory]
    [InlineData("/api/v1/operation-records", "export", HttpStatusCode.Forbidden)]
    [InlineData("/api/v1/operation-records/filter-options", "export", HttpStatusCode.Forbidden)]
    [InlineData("/api/v1/operation-records/export", "read", HttpStatusCode.Forbidden)]
    [InlineData("/api/v1/operation-records", null, HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/operation-records/filter-options", "read", HttpStatusCode.OK)]
    [InlineData("/api/v1/operation-records/export", "export", HttpStatusCode.OK)]
    public async Task Each_endpoint_is_guarded_by_its_configured_policy(string url, string? permission, HttpStatusCode expected)
    {
        var response = await GetAsync(url, permission);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>省略 offset、重复的多选键——与前端现有请求形状一致，绑定不能要求它们必填或只取第一个。</summary>
    [Fact]
    public async Task Query_parameters_bind_like_the_controller_did()
    {
        var response = await GetAsync(
            "/api/v1/operation-records?limit=5&keyword=ada&categories=account&categories=tenant&actions=user.created&outcome=failed",
            "read");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var input = _service.LastPaged!;
        Assert.Equal((0, 5, "ada", (OperationRecordOutcome?)OperationRecordOutcome.Failed), (input.Offset, input.Limit, input.Keyword, input.Outcome));
        Assert.Equal(["account", "tenant"], input.Categories);
        Assert.Equal(["user.created"], input.Actions);
    }

    [Fact]
    public async Task The_export_is_returned_as_a_file_with_the_configured_audit()
    {
        var response = await GetAsync("/api/v1/operation-records/export", "export");

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("records.csv", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(new OperationRecordExportAudit("operation-records.exported", "records.export"), _service.LastAudit);
    }

    private sealed class CapturingQueryService : IOperationRecordQueryService
    {
        public GetOperationRecordPagedInputDto? LastPaged { get; private set; }
        public OperationRecordExportAudit? LastAudit { get; private set; }

        public Task<PagedResult<OperationRecordOutputDto>> GetPagedListAsync(
            GetOperationRecordPagedInputDto input, CancellationToken cancellationToken = default)
        {
            LastPaged = input;
            return Task.FromResult(PagedResult<OperationRecordOutputDto>.Empty);
        }

        public Task<OperationRecordFilterOptionsOutputDto> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationRecordFilterOptionsOutputDto { Categories = [], Actions = [] });

        public Task<OperationRecordExportFileDto> ExportAsync(
            ExportOperationRecordsInputDto input, OperationRecordExportAudit audit, CancellationToken cancellationToken = default)
        {
            LastAudit = audit;
            return Task.FromResult(new OperationRecordExportFileDto([1, 2, 3], "text/csv", "records.csv"));
        }
    }

    // 请求头 X-Permission 决定主体带哪个权限声明；没有该头即匿名
    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Permission", out var permission))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("permission", permission.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
