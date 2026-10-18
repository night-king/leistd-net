using CompanyName.ProjectName.Application.Settings.Options;

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>界面显示的部署基线：空为跟随设备，非空必须是设置本身也接受的值。</summary>
public sealed class DisplayDefaultsOptionsValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Asia/Tokyo")]
    [InlineData(" Asia/Tokyo ")]
    [InlineData("UTC")]
    public void Empty_or_IANA_time_zones_are_accepted(string? timeZone)
    {
        var result = new DisplayDefaultsOptionsValidator().Validate(null, new DisplayDefaultsOptions { TimeZone = timeZone });

        Assert.True(result.Succeeded);
    }

    // Windows 时区 ID 后端认、浏览器不认：与写入设置时同一套判定
    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("China Standard Time")]
    public void Other_time_zones_fail_with_the_configuration_key(string timeZone)
    {
        var result = new DisplayDefaultsOptionsValidator().Validate(null, new DisplayDefaultsOptions { TimeZone = timeZone });

        Assert.True(result.Failed);
        Assert.Contains("DisplayDefaults:TimeZone", result.FailureMessage, StringComparison.Ordinal);
    }
#if (IncludeLocalization)

    [Theory]
    [InlineData(null, true)]
    [InlineData(" ", true)]
    [InlineData("en", true)]
    [InlineData("zh-CN", true)]
    [InlineData("fr", false)]
    [InlineData("zh-cn", false)]
    public void Only_supported_languages_are_accepted(string? language, bool accepted)
    {
        var result = new DisplayDefaultsOptionsValidator().Validate(null, new DisplayDefaultsOptions { Language = language });

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
            Assert.Contains("DisplayDefaults:Language", result.FailureMessage, StringComparison.Ordinal);
    }
#endif
}
