using Leistd.OperationRecords.Models;
using Xunit;

namespace Leistd.OperationRecords.Tests.Core;

/// <summary>失败原因的参数序列化：本组件替调用方写 JSON，写成什么形状是契约。</summary>
public sealed class OperationFailureTests
{
    [Fact]
    public void Placeholder_values_are_written_as_a_flat_json_object()
    {
        var failure = OperationFailure.FromCode("Order:CreditLimitExceeded", new Dictionary<string, object?>
        {
            ["Limit"] = 1000m,
            ["Available"] = 250,
            ["Requested"] = 300.5,
            ["Currency"] = "JPY",
            ["Blocked"] = true,
            ["Reason"] = null
        });

        Assert.Equal(
            """{"Limit":1000,"Available":250,"Requested":300.5,"Currency":"JPY","Blocked":true,"Reason":null}""",
            failure.Data);
    }

    // NaN 与正负无穷不是合法 JSON 数值：写成数值会抛异常，让"记录失败原因"本身失败
    [Fact]
    public void Non_finite_numbers_are_written_as_text_instead_of_throwing()
    {
        var failure = OperationFailure.FromCode("Quota:Exceeded", new Dictionary<string, object?>
        {
            ["Ratio"] = double.NaN,
            ["Upper"] = double.PositiveInfinity,
            ["Lower"] = float.NegativeInfinity
        });

        Assert.Equal("""{"Ratio":"NaN","Upper":"Infinity","Lower":"-Infinity"}""", failure.Data);
    }

    [Fact]
    public void Strings_are_escaped_rather_than_concatenated()
    {
        var failure = OperationFailure.FromCode("Order:Rejected", new Dictionary<string, object?>
        {
            ["Note"] = """he said "no", then left"""
        });

        // 手写 JSON 插值的那条路会在这里产出坏 JSON，展示端解析失败、整行原因渲染不出来
        // 默认编码器把引号写成 \u0022，不是 \"：两者都是合法 JSON，展示端 JSON.parse 一致
        Assert.Equal(@"{""Note"":""he said \u0022no\u0022, then left""}", failure.Data);
    }

    /// <summary>
    /// 复杂对象不摊开：这一列租户管理员直接可读、还会进导出，
    /// 而词条占位符也渲染不了嵌套对象。
    /// </summary>
    [Fact]
    public void Non_scalar_values_collapse_to_a_single_text_instead_of_being_expanded()
    {
        var failure = OperationFailure.FromCode("Order:Rejected", new Dictionary<string, object?>
        {
            ["Payload"] = new { Secret = "connection-string", Host = "db-01.internal" }
        });

        Assert.NotNull(failure.Data);
        Assert.DoesNotContain("connection-string", failure.Data);
        Assert.DoesNotContain("db-01.internal", failure.Data);
        Assert.Contains("AnonymousType", failure.Data);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_blank_code_degrades_to_none(string? code)
    {
        var failure = OperationFailure.FromCode(code, new Dictionary<string, object?> { ["X"] = 1 });

        Assert.True(failure.IsEmpty);
        Assert.Null(failure.Data);
    }

    [Fact]
    public void An_empty_parameter_set_stores_nothing()
    {
        Assert.Null(OperationFailure.FromCode("Order:Rejected").Data);
        Assert.Null(OperationFailure.FromCode("Order:Rejected", new Dictionary<string, object?>()).Data);
    }
}
