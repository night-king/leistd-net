#if (LocalIdentity)
using System.Text;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using Leistd.Security.OneTimeCodes;
using Microsoft.AspNetCore.DataProtection;

namespace CompanyName.ProjectName.UnitTests.Domain;

public sealed class TwoFactorDomainServiceTests
{
    [Fact]
    public void Unspecified_clock_values_are_interpreted_as_utc()
    {
        var service = new TwoFactorDomainService(new EphemeralDataProtectionProvider());
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var instant = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var code = Totp.ComputeCode(secret, Totp.TimeStepAt(instant));
        var protectedSecret = service.ProtectSecret(secret);
        var expected = service.VerifySetupCode(protectedSecret, code, instant.UtcDateTime);
        Assert.NotNull(expected);
        Assert.Equal(expected, service.VerifySetupCode(protectedSecret, code,
            DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Unspecified)));
    }
}
#endif
