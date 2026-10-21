using Xunit;

namespace Leistd.ExceptionHandling.Tests;

/// <summary>具名占位符填充：错误响应与操作记录的失败原因共用，口径变了两处一起变。</summary>
public class LocalizationPlaceholdersTests
{
    [Fact]
    public void Fills_named_placeholders_case_sensitively_and_keeps_unmatched_ones()
    {
        var text = LocalizationPlaceholders.Fill(
            "{Email} / {email} / {Count} / {Missing} / {Empty}",
            new Dictionary<string, object?> { ["Email"] = "a@b.com", ["Count"] = 3, ["Empty"] = null });

        Assert.Equal("a@b.com / {email} / 3 / {Missing} / ", text);
    }

    [Fact]
    public void Returns_the_text_unchanged_without_parameters()
        => Assert.Equal("{Email}", LocalizationPlaceholders.Fill("{Email}", null));
}
