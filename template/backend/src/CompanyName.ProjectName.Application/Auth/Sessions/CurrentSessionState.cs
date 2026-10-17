#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <inheritdoc cref="ICurrentSessionState" />
/// <remarks>按请求作用域登记：同一请求内的中间件、协议端点与应用服务读到同一个实例。</remarks>
internal sealed class CurrentSessionState : ICurrentSessionState
{
    /// <inheritdoc />
    public Guid? SessionId { get; private set; }

    /// <inheritdoc />
    public bool TwoFactorSetupRequired { get; private set; }

    /// <inheritdoc />
    public void Set(Guid sessionId, bool twoFactorSetupRequired)
    {
        SessionId = sessionId;
        TwoFactorSetupRequired = twoFactorSetupRequired;
    }
}
#endif
