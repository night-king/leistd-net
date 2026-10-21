using System.Net.Http.Json;
using System.Text.Json;
using Leistd.ExceptionHandling.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

/// <summary>预期失败的日志只记诊断字段：返回给调用方的 detail、默认文案与参数值都不进日志。</summary>
/// <remarks>
/// detail 面向提交请求的人，会回显其输入（邮箱等）；日志集中采集、保留更久，规范不允许联系方式进入。
/// 每个用例同时断言响应仍带该值，证明边界划在日志而不是响应上。
/// </remarks>
public sealed class BusinessExceptionHandlerLoggingTests
{
    private const string Code = "User:EmailAlreadyUsed";
    private const string SafeMessage = "Email already in use.";
    private const string Email = "zhang.san@example.com";

    [Fact]
    public async Task A_localized_detail_reaches_the_response_but_not_the_log()
    {
        using var host = await StartAsync(
            () => new BusinessException(Code, SafeMessage).WithData("Email", Email),
            localizer: new SingleEntryLocalizer(Code, "邮箱 '{Email}' 已被占用"));

        var detail = await GetDetailAsync(host);

        Assert.Equal($"邮箱 '{Email}' 已被占用", detail);
        AssertExpectedLogIsDiagnosticOnly(host);
    }

    // 默认文案即使拼进了邮箱（违反 BusinessException 契约），预期失败的日志也不记它
    [Fact]
    public async Task The_fallback_message_reaches_the_response_but_not_the_log()
    {
        var unsafeMessage = $"Email '{Email}' is already in use.";
        using var host = await StartAsync(() => new BusinessException(Code, unsafeMessage));

        Assert.Equal(unsafeMessage, await GetDetailAsync(host));
        AssertExpectedLogIsDiagnosticOnly(host);
    }

    [Fact]
    public async Task An_expected_failure_does_not_attach_its_exception_chain()
    {
        using var host = await StartAsync(
            () => new BusinessException(Code, SafeMessage, new InvalidOperationException($"lookup {Email} failed")));

        await GetDetailAsync(host);

        AssertExpectedLogIsDiagnosticOnly(host);
    }

    [Fact]
    public async Task A_server_side_business_failure_keeps_its_exception_but_not_the_detail()
    {
        using var host = await StartAsync(
            () => new BusinessException(Code, SafeMessage).WithData("Email", Email),
            localizer: new SingleEntryLocalizer(Code, "邮箱 '{Email}' 已被占用"),
            statusCode: StatusCodes.Status503ServiceUnavailable);

        Assert.Equal($"邮箱 '{Email}' 已被占用", await GetDetailAsync(host));

        var record = Assert.Single(HandlerRecords(host));
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.IsType<BusinessException>(record.Exception);
        Assert.Equal(Code, record.GetStructuredStateValue("Code"));
        Assert.DoesNotContain(Email, record.Message, StringComparison.Ordinal);
    }

    private static FakeLogRecord AssertExpectedLogIsDiagnosticOnly(IHost host)
    {
        var record = Assert.Single(HandlerRecords(host));
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Null(record.Exception);
        Assert.Equal(Code, record.GetStructuredStateValue("Code"));
        Assert.Equal("400", record.GetStructuredStateValue("StatusCode"));
        Assert.Equal(nameof(BusinessException), record.GetStructuredStateValue("ExceptionType"));
        Assert.Null(record.GetStructuredStateValue("Message"));
        Assert.False(string.IsNullOrEmpty(record.GetStructuredStateValue("TraceId")));

        foreach (var logged in host.Services.GetFakeLogCollector().GetSnapshot())
        {
            Assert.DoesNotContain(Email, logged.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                logged.StructuredState ?? [],
                pair => pair.Value?.Contains(Email, StringComparison.Ordinal) == true);
        }

        return record;
    }

    private static IEnumerable<FakeLogRecord> HandlerRecords(IHost host)
        => host.Services.GetFakeLogCollector().GetSnapshot()
            .Where(r => r.Category == typeof(AspNetCore.Handlers.BusinessExceptionHandler).FullName);

    private static async Task<string?> GetDetailAsync(IHost host)
    {
        using var response = await host.GetTestClient().GetAsync("/");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("detail").GetString();
    }

    private static async Task<IHost> StartAsync(
        Func<Exception> throwing,
        IStringLocalizer? localizer = null,
        int? statusCode = null)
        => await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddFakeLogging();
                    if (localizer is not null)
                        services.AddSingleton(localizer);
                    services.AddGlobalExceptionHandler(options =>
                    {
                        if (statusCode is { } mapped)
                            options.MapCode(Code, mapped);
                    });
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw throwing());
                }))
            .StartAsync();

    private sealed class SingleEntryLocalizer(string name, string value) : IStringLocalizer
    {
        public LocalizedString this[string key] => key == name
            ? new LocalizedString(key, value, false)
            : new LocalizedString(key, key, true);

        public LocalizedString this[string key, params object[] arguments] => this[key];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [this[name]];
    }
}
