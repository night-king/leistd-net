#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.ValueObjects;

namespace CompanyName.ProjectName.UnitTests.Domain;

/// <summary>独立因子与账号级恢复码：第一个独立因子带来恢复码，最后一个去掉时恢复码作废；通行密钥与身份验证器同等。</summary>
public sealed class IndependentFactorTests
{
    private static readonly string[] Codes = ["hash-1", "hash-2"];

    [Fact]
    public void The_first_passkey_issues_recovery_codes_and_satisfies_the_requirement()
    {
        var user = NewUser();

        user.RecordPasskeyRegistered(Codes);

        Assert.True(user.HasIndependentFactor);
        Assert.True(user.HasSecondFactor);
        Assert.False(user.TwoFactorEnabled);
        Assert.Equal(2, user.RecoveryCodesLeft);
        Assert.True(user.TryConsumeRecoveryCode("hash-1"));
        Assert.Equal(1, user.RecoveryCodesLeft);
    }

    [Fact]
    public void A_later_factor_keeps_the_existing_recovery_codes_and_must_not_bring_new_ones()
    {
        var user = NewUser();
        user.RecordPasskeyRegistered(Codes);

        Assert.Throws<ArgumentException>(() => user.EnableTwoFactor("secret", ["other"], 1));
        user.EnableTwoFactor("secret", null, 1);

        Assert.Equal(2, user.RecoveryCodesLeft);
        Assert.Throws<ArgumentException>(() => NewUser().EnableTwoFactor("secret", null, 1));
    }

    [Fact]
    public void Recovery_codes_go_only_with_the_last_independent_factor()
    {
        var user = NewUser();
        user.RecordPasskeyRegistered(Codes);
        user.RecordPasskeyRegistered(null);
        user.EnableTwoFactor("secret", null, 1);

        user.DisableTwoFactor();
        user.RecordPasskeyRemoved();
        Assert.Equal(2, user.RecoveryCodesLeft);

        user.RecordPasskeyRemoved();
        Assert.False(user.HasIndependentFactor);
        Assert.Equal(0, user.RecoveryCodesLeft);
        Assert.False(user.TryConsumeRecoveryCode("hash-1"));
    }

    [Fact]
    public void Removing_the_last_passkey_drops_a_passkey_preference()
    {
        var user = NewUser();
        user.RecordPasskeyRegistered(Codes);
        user.EnableTwoFactor("secret", null, 1);
        user.SetPreferredTwoFactorMethod(TwoFactorMethod.Passkey);

        user.RecordPasskeyRemoved();

        Assert.Null(user.PreferredTwoFactorMethod);
        Assert.True(user.HasIndependentFactor);
    }

    [Fact]
    public void A_passkey_can_be_preferred_only_while_one_is_registered()
    {
        var user = NewUser();

        Assert.ThrowsAny<Exception>(() => user.SetPreferredTwoFactorMethod(TwoFactorMethod.Passkey));
        user.RecordPasskeyRegistered(Codes);
        user.SetPreferredTwoFactorMethod(TwoFactorMethod.Passkey);

        Assert.Equal(TwoFactorMethod.Passkey, user.PreferredTwoFactorMethod);
    }

    [Fact]
    public void Each_passkey_change_rotates_the_security_stamp()
    {
        var user = NewUser();
        var before = user.SecurityStamp;
        user.RecordPasskeyRegistered(Codes);
        var registered = user.SecurityStamp;
        user.RecordPasskeyRemoved();

        Assert.NotEqual(before, registered);
        Assert.NotEqual(registered, user.SecurityStamp);
    }

    [Fact]
    public void A_reset_clears_passkeys_recovery_codes_and_the_authenticator()
    {
        var user = NewUser();
        user.RecordPasskeyRegistered(Codes);
        user.EnableTwoFactor("secret", null, 1);

        user.ResetSecondFactors();

        Assert.Equal(0, user.PasskeyCount);
        Assert.False(user.HasSecondFactor);
        Assert.Equal(0, user.RecoveryCodesLeft);
    }

    [Fact]
    public void Removing_a_passkey_that_was_never_registered_is_a_programming_error()
    {
        Assert.Throws<InvalidOperationException>(() => NewUser().RecordPasskeyRemoved());
    }

    private static User NewUser() => new("passkey_user", "passkey@example.test", "hash");
}
#endif
