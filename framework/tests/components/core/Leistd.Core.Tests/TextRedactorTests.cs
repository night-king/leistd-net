using Leistd.Redaction;
using Xunit;

namespace Leistd.Core.Tests;

/// <summary>敏感值的脱敏形态（写日志与对外展示共用）。</summary>
public sealed class TextRedactorTests
{
    /// <summary>邮箱保本地部开头几位 + 完整域名。</summary>
    [Theory]
    [InlineData("zhangsan@example.com", "zha***@example.com")]
    [InlineData("alice.smith+tag@sub.example.co.jp", "ali***@sub.example.co.jp")]
    // 数字也算
    [InlineData("123abc@example.com", "123***@example.com")]
    public void An_email_keeps_a_few_local_characters_and_the_whole_domain(string address, string expected)
        => Assert.Equal(expected, TextRedactor.RedactEmail(address));

    /// <summary>保留位数封顶三位，不随本地部变长而增加。</summary>
    /// <remarks>再多对识别的帮助有限，暴露面却线性上升。去掉封顶（只留半数规则），这条会红。</remarks>
    [Fact]
    public void The_kept_length_is_capped_at_three()
        => Assert.Equal("ale***@example.com", TextRedactor.RedactEmail("alexander.hamilton@example.com"));

    /// <summary>保留位数不超过本地部的一半。</summary>
    /// <remarks>
    /// 这条上限是硬的：只按固定位数留，<c>alice</c> 这类短本地部会被交出去大半，
    /// <c>bob</c> 更会变成 <c>bo***</c>，几乎等于没脱敏。去掉半数限制，这几条会红。
    /// </remarks>
    [Theory]
    [InlineData("alice@example.com", "al***@example.com")]
    [InlineData("bob@example.com", "b***@example.com")]
    [InlineData("al@example.com", "a***@example.com")]
    public void The_kept_length_never_exceeds_half_the_local_part(string address, string expected)
        => Assert.Equal(expected, TextRedactor.RedactEmail(address));

    /// <summary>从第一个字母或数字起算，不是从第一个字符。</summary>
    /// <remarks>
    /// 带引号的本地部是合法地址；从首字符起算会留下引号，认不出是谁，白担暴露面。
    /// 改成从 <c>local[0]</c> 起算，这两条会红。
    /// </remarks>
    [Theory]
    [InlineData("\"odd@local\"@example.com", "odd***@example.com")]
    [InlineData("_hidden@example.com", "hid***@example.com")]
    public void The_kept_characters_start_at_the_first_letter_or_digit(string address, string expected)
        => Assert.Equal(expected, TextRedactor.RedactEmail(address));

    /// <summary>按最后一个 <c>'@'</c> 切分，域名不会被截错。</summary>
    /// <remarks>改成按第一个 <c>'@'</c> 切，这条会红。</remarks>
    [Fact]
    public void An_email_is_split_at_the_last_at_sign()
        => Assert.Equal("odd***@example.com", TextRedactor.RedactEmail("\"odd@local\"@example.com"));

    /// <summary>本地部短到留不住任何一位时整体掩掉，域名照留。</summary>
    [Theory]
    [InlineData("a@b.io", "***@b.io")]
    [InlineData("@example.com", "***@example.com")]
    public void A_local_part_too_short_to_keep_anything_is_masked_entirely(string address, string expected)
        => Assert.Equal(expected, TextRedactor.RedactEmail(address));

    /// <summary>拿不到域名时不把原文回落出去。</summary>
    /// <remarks>
    /// 回落会让"配错的地址"成为唯一泄露原文的路径，而那恰好是最容易被翻到的一类日志——
    /// 地址配错时操作失败，排障的人正好在看它。改成回落原值，这两条会红。
    /// </remarks>
    [Theory]
    [InlineData("not-an-address", "not***")]
    [InlineData("trailing@", "tra***")]
    public void An_address_without_a_domain_is_not_passed_through(string address, string expected)
        => Assert.Equal(expected, TextRedactor.RedactEmail(address));

    /// <summary>空值与 null 返回空串，不造占位符。</summary>
    /// <remarks>本来没有值，给 <c>***</c> 会让人以为"有个值但被脱敏了"。</remarks>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_email_stays_empty(string? address)
        => Assert.Equal(string.Empty, TextRedactor.RedactEmail(address));

    /// <summary>部分脱敏保两端，位数由调用方给。</summary>
    /// <remarks>手机号 <c>(3, 2)</c>；卡号按 PCI DSS 最多 <c>(6, 4)</c>；证件号 <c>(0, 4)</c>。</remarks>
    [Theory]
    [InlineData("15812345690", 3, 2, "158***90")]
    [InlineData("1234567891011121", 6, 4, "123456***1121")]
    [InlineData("110101199003072316", 0, 4, "***2316")]
    public void A_partial_redaction_keeps_both_ends(string value, int start, int end, string expected)
        => Assert.Equal(expected, TextRedactor.RedactPartially(value, start, end));

    /// <summary>短到留不住两端时整体掩掉，不放过原值。</summary>
    /// <remarks>
    /// 否则参数配宽一点，短值就会整条泄露且不报错。把长度判断从 <c>&lt;=</c> 改成 <c>&lt;</c>，
    /// 等长那条会红。
    /// </remarks>
    [Theory]
    [InlineData("158", 3, 2)]
    [InlineData("1590", 3, 2)]
    // 恰好等于保留位数之和：整条都会被"保留"，等于没脱敏
    [InlineData("15890", 3, 2)]
    public void A_value_too_short_to_keep_both_ends_is_masked_entirely(string value, int start, int end)
        => Assert.Equal("***", TextRedactor.RedactPartially(value, start, end));

    /// <summary>保留位数为负直接拒绝，不静默当成 0。</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Negative_keep_counts_are_rejected(int start, int end)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => TextRedactor.RedactPartially("15812345690", start, end));

    /// <summary>部分脱敏的空值同样返回空串。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_value_stays_empty(string? value)
        => Assert.Equal(string.Empty, TextRedactor.RedactPartially(value, 3, 2));
}
