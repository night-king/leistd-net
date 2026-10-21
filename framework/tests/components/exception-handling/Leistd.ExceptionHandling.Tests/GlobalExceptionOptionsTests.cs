using Leistd.ExceptionHandling.Options;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

/// <summary>错误码映射只接受 4xx、5xx 状态。</summary>
/// <remarks>
/// 2xx、3xx 会把失败伪装成成功或跳转，调用方据状态码判断结果时会误读，因此在登记时就拒绝。
/// </remarks>
public sealed class GlobalExceptionOptionsTests
{
    [Theory]
    [InlineData(399)]
    [InlineData(600)]
    public void Mapping_a_code_to_a_non_error_status_is_rejected(int statusCode)
    {
        var options = new GlobalExceptionOptions();

        var host = Assert.Throws<ArgumentOutOfRangeException>(() => options.MapCode("OrderNotFound", statusCode));
        var component = Assert.Throws<ArgumentOutOfRangeException>(() => options.MapDefaultCode("OrderNotFound", statusCode));

        Assert.Equal("statusCode", host.ParamName);
        Assert.Equal("statusCode", component.ParamName);
        Assert.Empty(options.CodeStatusMappings);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(599)]
    public void Boundary_error_statuses_are_accepted(int statusCode)
    {
        var options = new GlobalExceptionOptions();

        options.MapCode("OrderNotFound", statusCode);
        options.MapDefaultCode("OrderConflict", statusCode);

        Assert.Equal(statusCode, options.CodeStatusMappings["OrderNotFound"]);
        Assert.Equal(statusCode, options.CodeStatusMappings["OrderConflict"]);
    }
}
