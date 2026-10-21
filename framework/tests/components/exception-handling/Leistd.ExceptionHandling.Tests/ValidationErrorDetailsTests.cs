using System.Text.Json;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

public class ValidationErrorDetailsTests
{
    [Fact]
    public void Serializes_to_camel_case_and_omits_null_machine_fields()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var withCode = JsonSerializer.Serialize(
            new ErrorItem("phone taken", "phone", "User:PhoneConflict"), options);
        Assert.Contains("\"detail\":", withCode);
        Assert.Contains("\"field\":", withCode);
        Assert.Contains("\"code\":\"User:PhoneConflict\"", withCode);

        var noCode = JsonSerializer.Serialize(new ErrorItem("required", "email", null), options);
        Assert.DoesNotContain("\"code\":", noCode);
    }
}
