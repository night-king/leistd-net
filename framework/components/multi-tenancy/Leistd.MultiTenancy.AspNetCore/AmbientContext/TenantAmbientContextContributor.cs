using Microsoft.Extensions.Options;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.AspNetCore.Resolution;
using Leistd.AmbientContext;
using Leistd.Security.Claims;

namespace Leistd.MultiTenancy.AspNetCore.AmbientContext;

// 非 HTTP 入口的租户维度：只从已验证主体的租户声明建立 ICurrentTenant，不读取请求参数。
// 不校验租户存在性与启用状态：那是请求入口（MultiTenancyMiddleware）的职责，
// 连接建立之后的失效判定走宿主授权策略。
internal sealed class TenantAmbientContextContributor(
    ICurrentTenant currentTenant,
    IOptions<ClaimTypeOptions> claimTypes) : IAmbientContextContributor
{
    /// <inheritdoc />
    public IDisposable? Enter(AmbientContextEnterContext context)
    {
        if (!context.Principal.HasAuthenticatedIdentity())
        {
            // 未认证：没有可信的租户来源，不猜，保持进入前的状态。
            return null;
        }

        // 无声明即宿主，仍要显式 Change(null)：入口可能继承了外层的租户上下文，
        // 不置位会让宿主主体读到别人的租户分区。非法声明失败关闭，不回退到宿主分区。
        return currentTenant.Change(TenantClaimReader.Read(context.Principal, claimTypes.Value));
    }

    // 捕获的是当前租户上下文本身，而不是主体的声明：在 Change 作用域里入队的任务要回到同一个租户。
    public object? Capture() => new CapturedTenant(currentTenant.Id, currentTenant.Name);

    public IDisposable? Restore(object? state)
        => state is CapturedTenant captured ? currentTenant.Change(captured.Id, captured.Name) : null;

    private sealed record CapturedTenant(Guid? Id, string? Name);
}
