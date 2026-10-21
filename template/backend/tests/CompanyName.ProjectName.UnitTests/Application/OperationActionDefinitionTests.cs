#if (LocalIdentity)
using CompanyName.ProjectName.Application;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using Leistd.OperationRecords;
using Leistd.OperationRecords.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>自证类动作（登录成功、改密码、注册）的目标就是本人：登记为 targetIsActor，界面据此把目标显示成操作人。</summary>
/// <remarks>
/// 登录失败刻意不标：它的目标是调用方提交的用户名，未经验证，标了就成了无需凭据即可写入任意文本的"操作人"。
/// </remarks>
public sealed class OperationActionDefinitionTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddLogging()
        .AddApplicationServices()
        .AddOperationRecords()
        .BuildServiceProvider();

    public void Dispose() => _provider.Dispose();

    [Theory]
    [InlineData(OperationRecordActions.AuthLoginSucceeded, true)]
    [InlineData(OperationRecordActions.AuthPasswordChanged, true)]
    [InlineData(OperationRecordActions.AuthRegistered, true)]
    [InlineData(OperationRecordActions.AuthLoginFailed, false)]
    [InlineData(OperationRecordActions.UserDeleted, false)]
    public void Only_self_acting_actions_treat_the_target_as_the_actor(string action, bool expected)
    {
        var definition = _provider.GetRequiredService<IOperationActionDefinitionManager>().GetOrNull(action);

        Assert.NotNull(definition);
        Assert.Equal(expected, definition.TargetIsActor);
    }
}
#endif
