#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.SecurityAlerts;

internal sealed class NullSecurityAlertPublisher : ISecurityAlertPublisher
{
    public Task PublishAsync(Guid userId, SecurityAlert alert, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
#endif
