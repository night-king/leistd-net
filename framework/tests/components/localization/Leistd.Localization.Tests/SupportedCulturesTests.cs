using System.Globalization;
using Leistd.Localization.AspNetCore;
using Leistd.Localization.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Localization.Tests;

/// <summary>支持语言只有 <see cref="JsonLocalizationOptions.SupportedCultures"/> 一份：默认语言、请求默认区域性与资源回落都取它的首项。</summary>
/// <remarks>
/// 三处各存一份时，宿主只改其中一处就会出现"请求按中文协商、词条却回落英文"这类不报错的分裂。
/// </remarks>
public sealed class SupportedCulturesTests
{
    private static ServiceProvider Build(Action<JsonLocalizationOptions>? configure = null) =>
        new ServiceCollection()
            .AddLogging()
            .AddJsonLocalization(configure)
            .BuildServiceProvider();

    [Fact]
    public void Defaults_to_english_then_chinese()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<JsonLocalizationOptions>>().Value;

        Assert.Equal(["en", "zh-CN"], options.SupportedCultures);
        Assert.Equal("en", options.DefaultCulture);
    }

    [Fact]
    public void First_supported_culture_drives_default_request_culture_and_resource_fallback()
    {
        using var provider = Build(options => options.SupportedCultures = ["zh-CN", "en"]);

        var json = provider.GetRequiredService<IOptions<JsonLocalizationOptions>>().Value;
        var request = provider.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;

        Assert.Equal("zh-CN", json.DefaultCulture);
        Assert.Equal("zh-CN", request.DefaultRequestCulture.Culture.Name);
        Assert.Equal("zh-CN", request.DefaultRequestCulture.UICulture.Name);
        Assert.Equal(["zh-CN", "en"], request.SupportedCultures!.Select(c => c.Name));
        Assert.Equal(["zh-CN", "en"], request.SupportedUICultures!.Select(c => c.Name));

        // 没有资源的界面语言回落到首项而不是英文
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fr");
            var text = provider.GetRequiredService<IStringLocalizer>()["Title:500"];
            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
            var chinese = provider.GetRequiredService<IStringLocalizer>()["Title:500"];

            Assert.False(text.ResourceNotFound);
            Assert.Equal(chinese.Value, text.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Empty_supported_cultures_fail_at_startup()
    {
        using var provider = Build(options => options.SupportedCultures = []);

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        var message = Assert.Single(failure.Failures);
        Assert.StartsWith("JsonLocalizationOptions.SupportedCultures", message, StringComparison.Ordinal);
    }

    // 每个无效名各报一条，运维一次能看全
    [Fact]
    public void Unknown_culture_names_fail_at_startup()
    {
        using var provider = Build(options => options.SupportedCultures = ["en", "xx-NOWHERE", " "]);

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal(2, failure.Failures.Count());
        Assert.Contains(failure.Failures, message => message.Contains("'xx-NOWHERE'", StringComparison.Ordinal));
    }
}
