#if (RemoteTokenAuth)
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CompanyName.ProjectName.Domain.Users.Abstractions;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Http;

namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>账户偏好端口的实现：读经 <see cref="IdentityAccountPreferenceCache"/>，写以 Token Exchange 代表本人转交 Identity。</summary>
/// <remarks>
/// <para>只能写本人：交换的主体就是当前请求的用户令牌，没有用户令牌时交换处理器直接拒绝，不退回机器身份。</para>
/// <para>写入的失败归类：Identity 明确的业务拒绝（带错误码的 400/403/404/409）原样交还调用方：同码、同状态，说明取 Identity 按用户语言给出的
/// <c>detail</c>（本服务有同码词条时按本地词条）。其余失败——包括不带错误码的 401/403、取令牌被拒、连接失败与超时——一律按依赖故障处理，
/// 不转本地 401：现有信号分不清用户凭证失效与交换配置错误，转成 401 会让会话有效的用户被前端登出。本服务自身的认证失败照常由认证中间件返回 401。</para>
/// </remarks>
/// <param name="writeApi">写入客户端（Token Exchange）。</param>
/// <param name="cache">读取缓存；写入成功后换成 Identity 返回的新表示。</param>
internal sealed class IdentityAccountPreferences(
    IIdentityAccountPreferencesWriteApi writeApi,
    IdentityAccountPreferenceCache cache) : IIdentityAccountPreferences
{
    public Task<AccountPreferenceSnapshot?> ReadAsync(
        Guid? tenantId,
        Guid userId,
        AccountPreferenceReadMode mode,
        CancellationToken cancellationToken = default)
    {
        var key = new AccountPreferenceCacheKey(tenantId, userId);
        return mode == AccountPreferenceReadMode.CurrentUser
            ? cache.ReadForegroundAsync(key, cancellationToken)
            : cache.ReadServerAsync(key, cancellationToken);
    }

    public async Task<AccountPreferenceSnapshot> WriteForCurrentUserAsync(
        Guid? tenantId,
        Guid userId,
        string field,
        string? value,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { [field] = value is null ? null : JsonValue.Create(value) };
        using var response = await writeApi.PatchAsync(body, CultureInfo.CurrentUICulture.Name, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var snapshot = (await response.ReadContentAsync<IdentityAccountPreferencesResponse>(cancellationToken: cancellationToken))?.ToSnapshot()
                           ?? throw new ServiceClientException("Identity returned an incomplete account preference representation.", failureKind: ServiceClientFailureKind.InvalidResponse);
            var etag = response.Headers.ETag?.ToString()
                       ?? throw new ServiceClientException("Identity returned account preferences without an ETag.", failureKind: ServiceClientFailureKind.InvalidResponse);
            cache.StoreAfterWrite(new AccountPreferenceCacheKey(tenantId, userId), snapshot, etag);
            return snapshot;
        }

        var failure = await response.CreateRemoteErrorAsync(cancellationToken);
        // 401 即使带码也不交还：转成本地 401 会让前端登出一个会话仍有效的用户
        if (failure.RemoteStatusCode is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.NotFound or (int)HttpStatusCode.Conflict
            && failure.ToBusinessException("Identity rejected the account preference change.") is { } rejection)
        {
            throw rejection.WithData("Value", value);
        }

        throw failure;
    }
}
#endif
