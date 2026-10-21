using Leistd.Data.Paging;
using Leistd.Notifications.Dtos;

namespace Leistd.Notifications.Stores;

/// <summary>持久化用户通知、已读状态和未读计数。</summary>
public interface INotificationStore
{
    /// <summary>保存通知。</summary>
    /// <param name="notification">已定案的用户通知：存储原样保存 <c>Id</c>、<c>CreationTime</c>、<c>IsRead</c>，不生成也不替换。</param>
    /// <param name="userId">收件人。</param>
    /// <param name="ct">取消令牌。</param>
    Task SaveAsync(NotificationOutputDto notification, string userId, CancellationToken ct = default);

    /// <summary>分页获取用户的通知，按创建时间倒序。</summary>
    /// <param name="userId">收件人。</param>
    /// <param name="page">分页；<see cref="PageRequest.Sorting"/> 不生效。</param>
    /// <param name="unreadOnly">只取未读。</param>
    /// <param name="ct">取消令牌。</param>
    Task<PagedResult<NotificationOutputDto>> GetByUserAsync(
        string userId,
        PageRequest page,
        bool unreadOnly = false,
        CancellationToken ct = default);

    /// <summary>标记单条通知为已读。</summary>
    Task MarkAsReadAsync(string notificationId, string userId, CancellationToken ct = default);

    /// <summary>标记用户所有通知为已读。</summary>
    Task MarkAllAsReadAsync(string userId, CancellationToken ct = default);

    /// <summary>获取用户未读通知数量。</summary>
    Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default);

    /// <summary>删除用户的一条通知；不存在或不属于该用户时返回 <see langword="false"/>。</summary>
    /// <param name="notificationId">通知标识。</param>
    /// <param name="userId">收件人：只能删自己的通知。</param>
    /// <param name="ct">取消令牌。</param>
    Task<bool> DeleteAsync(string notificationId, string userId, CancellationToken ct = default);

    /// <summary>删除用户的全部通知，返回删除条数。</summary>
    /// <remarks>用于清空通知，也用于删除账号时清理。</remarks>
    /// <param name="userId">收件人。</param>
    /// <param name="ct">取消令牌。</param>
    Task<int> DeleteAllAsync(string userId, CancellationToken ct = default);
}
