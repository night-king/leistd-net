using Leistd.Data.Paging;
using Leistd.ExceptionHandling;
using Leistd.Notifications.Errors;
using Leistd.Notifications.Stores;
using Leistd.Notifications.Dtos;
using Leistd.Security.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leistd.Notifications.AspNetCore.Endpoints;

/// <summary>当前用户的通知中心端点。</summary>
public static class NotificationEndpoints
{
    /// <summary>端点名前缀，宿主按名字给个别端点追加约定时使用。</summary>
    public const string NamePrefix = "Leistd.Notifications.";

    /// <summary>单次列表的最大条数。</summary>
    public const int MaximumListCount = 100;

    /// <summary>
    /// 映射通知中心：<c>GET /</c>、<c>GET /unread-count</c>、<c>PUT /{id}/read</c>、<c>PUT /read-all</c>、
    /// <c>DELETE /</c>、<c>DELETE /{id}</c>。
    /// </summary>
    /// <remarks>
    /// <para>全部端点走 <see cref="NotificationEndpointOptions.AccessPolicy"/>，且只作用于当前用户自己的通知：
    /// 收件人取自当前主体的用户标识，不接受参数指定。
    /// 当前身份不是用户（如机器客户端）时，读取返回空、写入返回带码的 403。</para>
    /// <para><c>GET /</c> 的 <c>maxCount</c> 收敛到 1–<see cref="MaximumListCount"/>，可加 <c>unreadOnly=true</c> 只取未读。
    /// 前缀由宿主的路由组决定；返回的路由组可继续追加约定。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapGroup("/api/v1/notifications").MapNotifications(options =&gt; options.AccessPolicy = "App.CurrentUser");
    /// </code>
    /// </example>
    /// <param name="endpoints">路由构建器（通常是宿主的路由组）。</param>
    /// <param name="configure">授权口径。</param>
    /// <returns>承载这组端点的路由组。</returns>
    public static RouteGroupBuilder MapNotifications(
        this IEndpointRouteBuilder endpoints,
        Action<NotificationEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NotificationEndpointOptions();
        configure(options);
        options.Validate();

        var group = endpoints.MapGroup(string.Empty).RequireAuthorization(options.AccessPolicy);

        group.MapGet(string.Empty, async (
                INotificationStore store,
                ICurrentUser currentUser,
                CancellationToken cancellationToken,
                int maxCount = 50,
                bool unreadOnly = false) =>
            {
                if (currentUser.Id is not { } userId)
                {
                    return (IReadOnlyList<NotificationOutputDto>)[];
                }

                var page = new PageRequest { Limit = Math.Clamp(maxCount, 1, MaximumListCount) };
                return (await store.GetByUserAsync(userId.ToString(), page, unreadOnly, cancellationToken)).Items;
            })
            .WithName(NamePrefix + "GetList");

        group.MapGet("unread-count", async (INotificationStore store, ICurrentUser currentUser, CancellationToken cancellationToken)
                => currentUser.Id is { } userId ? await store.GetUnreadCountAsync(userId.ToString(), cancellationToken) : 0)
            .WithName(NamePrefix + "GetUnreadCount");

        group.MapPut("{id}/read", async (string id, INotificationStore store, ICurrentUser currentUser, CancellationToken cancellationToken) =>
            {
                await store.MarkAsReadAsync(id, RequireUser(currentUser), cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName(NamePrefix + "MarkAsRead");

        group.MapPut("read-all", async (INotificationStore store, ICurrentUser currentUser, CancellationToken cancellationToken) =>
            {
                await store.MarkAllAsReadAsync(RequireUser(currentUser), cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName(NamePrefix + "MarkAllAsRead");

        group.MapDelete(string.Empty, async (INotificationStore store, ICurrentUser currentUser, CancellationToken cancellationToken) =>
            {
                await store.DeleteAllAsync(RequireUser(currentUser), cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName(NamePrefix + "DeleteAll");

        group.MapDelete("{id}", async (string id, INotificationStore store, ICurrentUser currentUser, CancellationToken cancellationToken) =>
            {
                await store.DeleteAsync(id, RequireUser(currentUser), cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName(NamePrefix + "Delete");

        return group;
    }

    private static string RequireUser(ICurrentUser currentUser)
        => currentUser.Id?.ToString()
           ?? throw new BusinessException(NotificationErrorCodes.IdentityCannotOperate, "The current identity cannot operate on user notifications.");
}
