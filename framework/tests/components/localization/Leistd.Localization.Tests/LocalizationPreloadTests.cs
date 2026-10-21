using System.Globalization;
using Leistd.Localization.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.Localization.Tests;

/// <summary>启动期资源预热：把首个请求才会触发的解析提前到启动阶段。</summary>
/// <remarks>
/// <para>这条路径的失败方式是<b>静默降级</b>——资源坏了照样启动，只在日志里留一行。
/// 因此断言的对象只能是日志本身，用官方 <see cref="FakeLogger"/> 而不是手写替身。</para>
/// <para>预热服务是 <c>internal</c>，这里经宿主的托管服务集合取得——那正是它唯一的真实入口。</para>
/// </remarks>
public class LocalizationPreloadTests
{
    private static ServiceProvider Build(string[] cultures, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddFakeLogging());
        services.AddJsonLocalization(options => options.SupportedCultures = cultures);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static IHostedService PreloadService(IServiceProvider provider) =>
        Assert.Single(
            provider.GetServices<IHostedService>(),
            s => s.GetType().Name.Contains("LocalizationPreload", StringComparison.Ordinal));

    [Fact]
    public void Preload_service_is_registered_by_the_localization_entry_point()
    {
        using var provider = Build(["en", "zh-CN"]);

        Assert.NotNull(PreloadService(provider));
    }

    // 每个支持的界面语言都要预热一次；少一条就意味着那门语言的资源问题
    // 要拖到生产的首个请求才暴露。
    [Fact]
    public async Task Every_supported_ui_culture_is_warmed_up_and_logged()
    {
        using var provider = Build(["en", "zh-CN"]);
        var collector = provider.GetRequiredService<FakeLogCollector>();

        await PreloadService(provider).StartAsync(CancellationToken.None);

        var messages = collector.GetSnapshot()
            .Where(r => r.Message.Contains("warm-up", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Message)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.Contains(messages, m => m.Contains("'en'", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("'zh-CN'", StringComparison.Ordinal));
    }

    // 预热清单复用 RequestLocalizationOptions.SupportedUICultures，宿主改它必须生效——
    // 否则新增语言永远不会被预热，而配置看起来是对的。
    [Fact]
    public async Task Warm_up_list_follows_the_host_request_localization_options()
    {
        using var provider = Build(["en"], services => services.Configure<RequestLocalizationOptions>(
            o => o.SupportedUICultures = [new CultureInfo("ja"), new CultureInfo("de")]));
        var collector = provider.GetRequiredService<FakeLogCollector>();

        await PreloadService(provider).StartAsync(CancellationToken.None);

        var messages = collector.GetSnapshot().Select(r => r.Message).ToArray();
        Assert.Contains(messages, m => m.Contains("'ja'", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("'de'", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("'en'", StringComparison.Ordinal));
    }

    // 已取消的启动令牌必须让预热中止：宿主启动失败时不该继续读一堆资源文件。
    [Fact]
    public async Task Cancelled_start_stops_the_warm_up_loop()
    {
        using var provider = Build(["en", "zh-CN", "ja"]);
        var collector = provider.GetRequiredService<FakeLogCollector>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await PreloadService(provider).StartAsync(cts.Token);

        Assert.DoesNotContain(collector.GetSnapshot(),
            r => r.Message.Contains("warm-up", StringComparison.OrdinalIgnoreCase));
    }

    // 没有配置任何界面语言时安静返回，不抛也不写日志。
    [Fact]
    public async Task No_supported_cultures_is_a_quiet_no_op()
    {
        using var provider = Build(["en"], services => services.Configure<RequestLocalizationOptions>(
            o => o.SupportedUICultures = null));
        var collector = provider.GetRequiredService<FakeLogCollector>();

        await PreloadService(provider).StartAsync(CancellationToken.None);

        Assert.DoesNotContain(collector.GetSnapshot(),
            r => r.Message.Contains("warm-up", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stopping_is_a_no_op()
    {
        using var provider = Build(["en"]);

        await PreloadService(provider).StopAsync(CancellationToken.None);
    }
}
