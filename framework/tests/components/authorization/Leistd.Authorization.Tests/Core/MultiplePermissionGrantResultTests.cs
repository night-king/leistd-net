
using Xunit;
using Leistd.Authorization.Checking;

namespace Leistd.Authorization.Tests.Core;

/// <summary>多权限结果在空集合上必须失败关闭。</summary>
/// <remarks>
/// <c>Enumerable.All</c> 对空序列返回 <c>true</c>（全称量化在空集上恒真）。
/// 直接沿用会让 <c>AllGranted</c> 成为失败开放的授权判据——
/// <c>IsGrantedAsync(string[])</c> 对 null、空数组与全空白入参都返回空结果字典，
/// 于是 <c>names</c> 意外为空时（拼错常量、配置漏读、上游返回空列表）当场放行。
/// </remarks>
public class MultiplePermissionGrantResultTests
{
    [Fact]
    public void AllGranted_is_false_for_an_empty_result()
    {
        var result = new MultiplePermissionGrantResult(new Dictionary<string, bool>());

        Assert.False(result.AllGranted);
        Assert.False(result.AnyGranted);
    }

    [Fact]
    public void AllGranted_is_true_only_when_every_checked_permission_is_granted()
    {
        var allGranted = new MultiplePermissionGrantResult(
            new Dictionary<string, bool> { ["a"] = true, ["b"] = true });
        var partial = new MultiplePermissionGrantResult(
            new Dictionary<string, bool> { ["a"] = true, ["b"] = false });

        Assert.True(allGranted.AllGranted);
        Assert.False(partial.AllGranted);
        Assert.True(partial.AnyGranted);
    }

    [Fact]
    public void AnyGranted_is_false_when_nothing_is_granted()
    {
        var result = new MultiplePermissionGrantResult(
            new Dictionary<string, bool> { ["a"] = false, ["b"] = false });

        Assert.False(result.AnyGranted);
        Assert.False(result.AllGranted);
    }
}
