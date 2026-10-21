#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.UnitTests.Domain;

/// <summary>密码策略：服务端唯一权威，不建宿主就能验的典型领域规则。</summary>
/// <remarks>
/// 这是单元测试该覆盖的形状——纯函数、没有依赖、分支全部可枚举。
/// 超级管理员初始化曾只检查口令非空，因此各服务端入口统一使用 PasswordPolicy 校验。
/// 纯类断言不需要集成测试宿主，实际管理员初始化入口由集成测试 DefaultAdminBootstrapTests 验证。
/// </remarks>
public class PasswordPolicyTests
{
    [Theory]
    [InlineData("aVeryLongPassphrase")]
    [InlineData("correct horse battery staple and then some")]
    [InlineData("123456789012345")]                 // 只看长度，不要求复杂度
    [InlineData("            x")]                    // 空格计入长度
    public void Long_enough_passwords_are_accepted(string password)
    {
        Assert.True(PasswordPolicy.IsAcceptable(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("elevenchars")]
    public void Missing_or_short_passwords_are_rejected(string? password)
    {
        Assert.False(PasswordPolicy.IsAcceptable(password));
    }

    // 边界差一位就是策略被绕过：长度下限必须精确到字符。
    [Fact]
    public void Minimum_length_boundary_is_exact()
    {
        Assert.False(PasswordPolicy.IsAcceptable(new string('a', PasswordPolicy.MinimumLength - 1)));
        Assert.True(PasswordPolicy.IsAcceptable(new string('a', PasswordPolicy.MinimumLength)));
    }

    // 上限只为防御哈希开销型拒绝服务，同样要精确。
    [Fact]
    public void Maximum_length_boundary_is_exact()
    {
        Assert.True(PasswordPolicy.IsAcceptable(new string('a', PasswordPolicy.MaximumLength)));
        Assert.False(PasswordPolicy.IsAcceptable(new string('a', PasswordPolicy.MaximumLength + 1)));
    }

    // 策略只管长度：够长就接受，即使看起来是常见形态。要拦已泄漏口令由业务项目接泄漏库，
    // 在本类之外加一步校验（见 PasswordPolicy 的说明）。
    [Fact]
    public void A_long_enough_password_is_accepted_even_if_it_looks_weak()
    {
        Assert.True(PasswordPolicy.IsAcceptable("Admin@123456"));
        Assert.True(PasswordPolicy.IsAcceptable("123456789012"));

    }

    [Fact]
    public void Ensure_passes_silently_for_an_acceptable_password()
    {
        PasswordPolicy.Ensure("aVeryLongPassphrase", "DefaultAdmin:Password");
    }
}
#endif
