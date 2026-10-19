using Microsoft.AspNetCore.SignalR;
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
using Leistd.RealTime.Subscriptions;

namespace Leistd.RealTime.Tests.TestDoubles;

internal sealed class RecordingGroupManager : IGroupManager
{
    public List<(string ConnectionId, string GroupName)> Added { get; } = [];
    public List<(string ConnectionId, string GroupName)> Removed { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    public Exception? Failure { get; set; }

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Added.Add((connectionId, groupName));
        Tokens.Add(cancellationToken);
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Removed.Add((connectionId, groupName));
        Tokens.Add(cancellationToken);
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}

internal sealed class ThrowingAuthorizer : IRealTimeSubscriptionAuthorizer
{
    public Task<bool> AuthorizeAsync(RealTimeSubscriptionContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Authorization should not be evaluated for public subscriptions.");
}

/// <summary>授权器自身出错（依赖不可用等）：这是故障，不是拒绝。</summary>
internal sealed class FailingAuthorizer : IRealTimeSubscriptionAuthorizer
{
    public Task<bool> AuthorizeAsync(RealTimeSubscriptionContext context, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Authorization store unavailable.");
}

internal sealed class DenyingAuthorizer : IRealTimeSubscriptionAuthorizer
{
    public Task<bool> AuthorizeAsync(RealTimeSubscriptionContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

internal sealed class TestHubContext(IHubClients clients) : IHubContext<RealTimeHub>
{
    public IHubClients Clients { get; } = clients;
    public IGroupManager Groups { get; } = new RecordingGroupManager();
}

internal sealed class RecordingHubClients(IClientProxy groupProxy) : IHubClients
{
    public string? LastGroupName { get; private set; }
    public IClientProxy All => groupProxy;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => groupProxy;
    public ISingleClientProxy Client(string connectionId) => new RecordingSingleClientProxy();
    IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => groupProxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => groupProxy;
    public IClientProxy Group(string groupName)
    {
        LastGroupName = groupName;
        return groupProxy;
    }

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => groupProxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => groupProxy;
    public IClientProxy User(string userId) => groupProxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => groupProxy;
}

internal sealed class RecordingSingleClientProxy : ISingleClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken) => Task.FromResult(default(T)!);
}

internal sealed class RecordingClientProxy : IClientProxy
{
    public List<(string Method, object?[] Args)> Sent { get; } = [];

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        Sent.Add((method, args));
        return Task.CompletedTask;
    }
}
