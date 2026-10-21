using Leistd.Authorization.Resource.Abstractions;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Leistd.Authorization.Resource.AspNetCore.Operations;

// 业务入口：以当前主体调用官方 IAuthorizationService。没有经过认证的当前主体一律拒绝，
// 后台任务要判定资源时须先经 IAmbientContext.Begin(principal) 建立主体。
internal sealed class ResourceAuthorizationService(
    IAuthorizationService authorizationService,
    ICurrentPrincipalAccessor principalAccessor) : IResourceAuthorizationService
{
    public Task<bool> IsGrantedAsync<TResource>(TResource resource, string operation)
        where TResource : IAuthorizableResource
        => resource is null
            ? Task.FromResult(false)
            : IsGrantedAsync(resource, resource.ResourceName, resource.ResourceKey, operation);

    public async Task<bool> IsGrantedAsync<TResource>(
        TResource resource,
        string resourceName,
        string resourceKey,
        string operation)
    {
        if (resource is null ||
            string.IsNullOrWhiteSpace(resourceName) ||
            string.IsNullOrWhiteSpace(resourceKey) ||
            string.IsNullOrWhiteSpace(operation))
        {
            return false;
        }

        var principal = principalAccessor.Principal;
        // 与官方 DenyAnonymousAuthorizationRequirement 同一判据：任一身份已认证即可
        if (!principal.HasAuthenticatedIdentity())
        {
            return false;
        }

        var result = await authorizationService.AuthorizeAsync(
            principal, resource, new ResourceOperationRequirement(resourceName, resourceKey, operation));
        return result.Succeeded;
    }
}
