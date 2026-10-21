#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.DomainServices;
#if (OpenIddictServer)
using OpenIddict.Abstractions;
#endif

namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <summary>作废用户已建立的会话与已签发的令牌：管理员停用、重置口令、删除与本人找回口令共用。</summary>
/// <remarks>
/// <para>撤权要对已签发的凭据生效：会话 Cookie 由会话校验按登记的会话拒绝，Bearer 令牌由令牌记录校验按撤销状态拒绝，
/// 两者都在认证阶段就失效。调用方在工作单元内、把它放在最后一步。</para>
/// <para>会话删除与调用方的写入同在一个事务里提交。</para>
#if (OpenIddictServer)
/// <para>令牌撤销不在这个事务里：OpenIddict 存储用自己的上下文与连接（多租户时用户还在另一个库），
/// 撤销语句当场生效。所以它放在最后、提交之前：撤不掉就抛出，整个用例回滚，不留"账号停了、令牌还能用"的状态；
/// 撤销之后只剩提交本身可能失败，那时令牌已作废而用例回滚，结果是本人需要重新登录，错在安全一侧。
/// 不放到提交后处理器：那里失败时变更已提交、令牌仍然有效，而重置两步验证与删除的重试会在前置检查处返回，不再撤销。</para>
#endif
/// </remarks>
internal sealed class UserAccessRevoker(
#if (OpenIddictServer)
    UserSessionDomainService userSessionDomainService,
    IOpenIddictTokenManager tokenManager)
#else
    UserSessionDomainService userSessionDomainService)
#endif
{
    /// <param name="userId">用户 Id。</param>
    /// <param name="keepSessionId">保留的会话（管理员操作的是自己时保留当前这台），没有则为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RevokeAllAsync(Guid userId, Guid? keepSessionId, CancellationToken cancellationToken)
    {
        await userSessionDomainService.RevokeAllAsync(userId, keepSessionId, cancellationToken);
#if (OpenIddictServer)
        // 官方批量撤销：EF 存储里是一条 ExecuteUpdate，不必先把该主体的令牌全部读进内存
        await tokenManager.RevokeBySubjectAsync(userId.ToString(), cancellationToken);
#endif
    }
}
#endif
