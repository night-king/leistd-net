#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>当前用户的登录设备：查看与撤销。</summary>
public interface IUserSessionAppService : IAppService
{
    /// <summary>当前用户仍然有效的会话，最近活跃的在前。</summary>
    Task<IReadOnlyList<UserSessionOutputDto>> GetCurrentUserSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>撤销自己的某个会话（该设备随即需要重新登录）。</summary>
    /// <remarks>会话已不存在时静默成功：撤销是幂等的，也不借此透露别人的会话 Id 是否存在。</remarks>
    /// <exception cref="Leistd.ExceptionHandling.BusinessException">撤销的是当前会话——那应当走退出登录。</exception>
    Task RevokeCurrentUserSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>撤销除当前会话以外的全部会话，返回被退出的有效设备数（已过期的会话一并清理，不计入）。</summary>
    Task<int> RevokeOtherCurrentUserSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>当前会话的开始时间（即这次登录的时刻，完整精度）；未登录或会话已不存在时为 <see langword="null"/>。</summary>
    /// <remarks>认证时间声明只到秒，判断"某一时刻之后是否发生过新的登录"要用它。</remarks>
    Task<DateTime?> GetCurrentSessionStartTimeAsync(CancellationToken cancellationToken = default);

    /// <summary>结束当前会话（退出登录时调用；未登录或会话已不存在时什么也不做）。</summary>
    Task EndCurrentSessionAsync(CancellationToken cancellationToken = default);
}
#endif
