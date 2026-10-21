using Xunit;

namespace Leistd.ExceptionHandling.Tests;

public class BusinessExceptionCodeTests
{
    [Fact]
    public void Constructor_requires_a_stable_code_and_safe_message()
    {
        Assert.Throws<ArgumentException>(() => new BusinessException("", "message"));
        Assert.Throws<ArgumentException>(() => new BusinessException("Order:Rejected", ""));
    }

    [Fact]
    public void Constructor_fixes_the_error_identity()
    {
        var exception = new BusinessException("Order:Rejected", "The order was rejected.");

        Assert.Equal("Order:Rejected", exception.Code);
        Assert.Equal("The order was rejected.", exception.Message);
    }

    [Fact]
    public void WithData_accumulates_localization_arguments()
    {
        var exception = new BusinessException("Order:StockInsufficient", "There is not enough stock.")
            .WithData("Sku", "A1")
            .WithData("Qty", 3);

        Assert.Equal("A1", exception.LocalizationData["Sku"]);
        Assert.Equal(3, exception.LocalizationData["Qty"]);
    }
}
