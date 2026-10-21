#if (LocalIdentity)
using System.Data.Common;
using CompanyName.ProjectName.Infrastructure.TenantConnections.Provider;
using Leistd.MultiTenancy.Errors;

namespace CompanyName.ProjectName.UnitTests.Infrastructure;

/// <summary>开通失败的数据库错误翻译：只翻译调用方能改的四种，其余不翻译。</summary>
/// <remarks>
/// 把库重启、连接数耗尽、序列化失败也翻成 400，会让责任归属出错——客户端既不会重试也不会告警，
/// 而运维在 4xx 面板上根本看不到这台库出了事。
/// </remarks>
public sealed class TenantDatabaseErrorDescriberTests
{
    private readonly PostgresTenantDatabaseErrorDescriber _describer = new();

    [Theory]
    [InlineData("3D000", MultiTenancyErrorCodes.DedicatedDatabaseMissing)]
    [InlineData("42P01", MultiTenancyErrorCodes.DedicatedDatabaseNotMigrated)]
    [InlineData("28000", MultiTenancyErrorCodes.DedicatedDatabaseRejected)]
    [InlineData("28P01", MultiTenancyErrorCodes.DedicatedDatabaseRejected)]
    public void Caller_fixable_states_are_translated_to_coded_business_failures(string sqlState, string expectedCode)
    {
        var described = _describer.Describe(new FakeDbException(sqlState));

        Assert.NotNull(described);
        Assert.Equal(expectedCode, described.Code);
    }

    /// <summary>没有 SQLSTATE 即连接阶段就失败了（主机不可达、端口拒绝），连接串是调用方给的。</summary>
    [Fact]
    public void A_connection_failure_is_translated_as_unreachable()
    {
        var described = _describer.Describe(new FakeDbException(string.Empty));

        Assert.Equal(MultiTenancyErrorCodes.DedicatedDatabaseUnreachable, described?.Code);
    }

    /// <summary>基础设施与瞬时故障不翻译：40001 序列化失败、53300 连接数耗尽、57P01 管理员关库。</summary>
    [Theory]
    [InlineData("40001")]
    [InlineData("53300")]
    [InlineData("57P01")]
    public void Infrastructure_failures_are_left_to_the_5xx_path(string sqlState)
        => Assert.Null(_describer.Describe(new FakeDbException(sqlState)));

    /// <summary>驱动异常常被 EF 或工作单元包上几层。</summary>
    [Fact]
    public void A_nested_driver_exception_is_still_found()
    {
        var wrapped = new InvalidOperationException("save failed", new FakeDbException("3D000"));

        Assert.Equal(MultiTenancyErrorCodes.DedicatedDatabaseMissing, _describer.Describe(wrapped)?.Code);
    }

    /// <summary>不是数据库原因的异常照常往外抛。</summary>
    [Fact]
    public void A_non_database_exception_is_not_translated()
        => Assert.Null(_describer.Describe(new InvalidOperationException("boom")));

    private sealed class FakeDbException(string sqlState) : DbException("database failure")
    {
        public override string SqlState => sqlState;
    }
}
#endif
