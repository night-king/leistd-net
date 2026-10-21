using Leistd.Localization.Json;
using MsOptions = Microsoft.Extensions.Options.Options;
using Leistd.Localization.Options;
using Xunit;

namespace Leistd.Localization.Tests;

/// <summary>
/// 读取器的健壮性：坏 JSON 被跳过而非拖垮整体；culture 声明与文件名不一致时仍加载（仅告警）。
/// 夹具资源嵌入在本测试程序集的 TestResources/{Case} 目录，用 ResourcesPath 精确定位单个文件。
/// </summary>
public class JsonLocalizationResourceReaderRobustnessTests
{
    private static JsonLocalizationResourceReader CreateReader(string resourcesPath)
    {
        var options = MsOptions.Create(new JsonLocalizationOptions { ResourcesPath = resourcesPath });
        options.Value.ResourceAssemblies.Add(typeof(JsonLocalizationResourceReaderRobustnessTests).Assembly);
        // logger 省略：走 null → NullLogger，验证不依赖日志断言
        return new JsonLocalizationResourceReader(options);
    }

    [Fact]
    public void Malformed_json_is_skipped_and_yields_empty_without_throwing()
    {
        var reader = CreateReader("TestResources/Broken");

        // 坏文件不抛异常，返回空表（跳过），不影响进程
        var texts = reader.GetTexts("en");

        Assert.Empty(texts);
    }

    [Fact]
    public void Culture_mismatch_still_loads_texts()
    {
        // 文件名解析为 en，但内部声明 zh-CN → 告警但仍加载键值（避免因笔误整份丢失）
        var reader = CreateReader("TestResources/Mismatch");

        var texts = reader.GetTexts("en");

        Assert.Equal("misdeclared", texts["Greeting"]);
    }

    [Fact]
    public void Well_formed_resource_loads_normally()
    {
        var reader = CreateReader("TestResources/Good");

        var texts = reader.GetTexts("en");

        Assert.Equal("Hello", texts["Greeting"]);
    }

    [Fact]
    public void Multiple_candidates_for_same_culture_throw_instead_of_silently_picking_first()
    {
        // 两个清单名（FirstArea/SecondArea）都以 .Ambiguous.en.json 结尾 → 精确后缀命中多候选，应抛错暴露歧义。
        var reader = CreateReader("Ambiguous");

        var ex = Assert.Throws<InvalidOperationException>(() => reader.GetTexts("en"));
        Assert.Contains("Ambiguous", ex.Message, StringComparison.Ordinal);
    }
}
