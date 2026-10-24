using System.Security.Cryptography;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Roles.Dtos;
using CompanyName.ProjectName.Application.Users.AppServices;
using CompanyName.ProjectName.Application.Users.Dtos;
using Leistd.OperationRecords.AspNetCore.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>用户管理控制器。</summary>
[Authorize]
[Route("api/v1/users")]
public sealed class UserController(IUserAppService userAppService) : BaseController
{
    /// <summary>获取用户列表（需要用户查看权限）。</summary>
    [HttpGet]
    [Authorize(Policy = PermissionConstant.Users.Default)]
    public async Task<PagedResult<UserManagementOutputDto>> GetPagedListAsync(
        [FromQuery] GetUserPagedInputDto input,
        CancellationToken cancellationToken)
    {
        return await userAppService.GetPagedListAsync(input, cancellationToken);
    }

    /// <summary>获取用户详情（需要用户查看权限）。</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = PermissionConstant.Users.Default)]
    public async Task<UserManagementOutputDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await userAppService.GetAsync(id, cancellationToken);
    }

#if (LocalIdentity)
    /// <summary>创建用户（需要用户创建权限）。</summary>
    [HttpPost]
    [Authorize(Policy = PermissionConstant.Users.Create)]
    // 创建类端点还没有目标标识，省略路由键，被拒记录的目标记为 "-"
    [OperationRecordAction(OperationRecordActions.UserCreated)]
    public async Task<UserManagementOutputDto> CreateAsync(
        [FromBody] CreateUserInputDto input,
        CancellationToken cancellationToken)
    {
        return await userAppService.CreateAsync(input, cancellationToken);
    }

    /// <summary>更新用户（需要用户更新权限）。</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserUpdated, "id")]
    public async Task<UserManagementOutputDto> UpdateAsync(
        Guid id,
        [FromBody] UpdateUserInputDto input,
        CancellationToken cancellationToken)
    {
        return await userAppService.UpdateAsync(id, input, cancellationToken);
    }

#endif
    /// <summary>启用用户（需要用户更新权限）。</summary>
    [HttpPatch("{id}/enable")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserEnabled, "id")]
    public async Task EnableAsync(Guid id, CancellationToken cancellationToken)
    {
        await userAppService.EnableAsync(id, cancellationToken);
    }

    /// <summary>禁用用户（需要用户更新权限）。</summary>
    [HttpPatch("{id}/disable")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserDisabled, "id")]
    public async Task DisableAsync(Guid id, CancellationToken cancellationToken)
    {
        await userAppService.DisableAsync(id, cancellationToken);
    }

    /// <summary>重置用户密码（需要用户更新权限）。</summary>
#if (LocalIdentity)
    [HttpPost("{id}/reset-password")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserPasswordReset, "id")]
    public async Task ResetPasswordAsync(
        Guid id,
        [FromBody] ResetUserPasswordInputDto input,
        CancellationToken cancellationToken)
    {
        await userAppService.ResetPasswordAsync(id, input, cancellationToken);
    }

    /// <summary>解除用户的登录锁定（需要用户更新权限）。</summary>
    [HttpPost("{id}/unlock")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserUnlocked, "id")]
    public async Task UnlockAsync(Guid id, CancellationToken cancellationToken)
    {
        await userAppService.UnlockAsync(id, cancellationToken);
    }

    /// <summary>重置用户的两步验证（需要用户更新权限；不能重置自己；管理员的会话不够新时带上自己的当前口令）。</summary>
    [HttpPost("{id}/reset-two-factor")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserTwoFactorReset, "id")]
    public async Task ResetTwoFactorAsync(Guid id, [FromBody] ResetUserTwoFactorInputDto input, CancellationToken cancellationToken)
    {
        await userAppService.ResetTwoFactorAsync(id, input, cancellationToken);
    }
#endif

#if (LocalIdentity)
    /// <summary>删除用户（需要用户删除权限；幂等：不存在时同样成功）。</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = PermissionConstant.Users.Delete)]
    // 目标标识取路由上的 id，与成功路径写下的值逐字一致，按目标检索才查得全
    [OperationRecordAction(OperationRecordActions.UserDeleted, "id")]
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await userAppService.DeleteAsync(id, cancellationToken);
    }

#endif

    /// <summary>取用户上传的头像图片（已登录即可）。</summary>
    /// <remarks>
    /// <para>DTO 里的头像地址指向这里（带内容摘要作版本号，见 <c>AvatarUrls</c>），图片本身不进 DTO。
    /// 请求版本匹配当前内容时按不可变资源长期缓存，其他地址每次重新验证；ETag 用于条件请求。</para>
    /// <para>不要求用户管理权限：头像随用户名出现在各处，查询仍受租户过滤器约束。
    /// 外部地址的头像不经过这里，DTO 直接给出原地址。</para>
    /// </remarks>
    [HttpGet("{id}/avatar")]
    public async Task<IActionResult> GetAvatarAsync(Guid id, [FromQuery] string? v, CancellationToken cancellationToken)
    {
        var avatar = await userAppService.GetAvatarAsync(id, cancellationToken);
        if (avatar is null)
        {
            // 清除后再上传同一图片会复用地址，不让 404 进入缓存。
            Response.Headers.CacheControl = "private, no-store";
            return NotFound();
        }

        var entityTag = new EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(avatar.Content))[..16]}\"");
        Response.Headers.CacheControl = string.Equals(v, avatar.Version, StringComparison.Ordinal)
            ? "private, max-age=31536000, immutable"
            : "private, no-cache";
        return File(avatar.Content, avatar.ContentType, lastModified: null, entityTag: entityTag);
    }

    /// <summary>查询用户角色（需要角色分配权限）。</summary>
    [HttpGet("{id}/roles")]
    [Authorize(Policy = PermissionConstant.Users.ManageRoles)]
    public async Task<IReadOnlyList<RoleBriefOutputDto>> GetRolesAsync(Guid id, CancellationToken cancellationToken)
    {
        return await userAppService.GetRolesAsync(id, cancellationToken);
    }

    /// <summary>替换用户角色（需要角色分配权限）。</summary>
    /// <remarks>
    /// 与 <c>PUT /api/v1/users/{id}</c> 是两个独立命令：只持有 Users.Update 的主体
    /// 无法改变任何人的角色，只持有 Users.ManageRoles 的主体也无法修改用户资料。
    /// </remarks>
    [HttpPut("{id}/roles")]
    [Authorize(Policy = PermissionConstant.Users.ManageRoles)]
    // 目标标识取路由上的 id，与成功路径写下的值逐字一致，按目标检索才查得全
    [OperationRecordAction(OperationRecordActions.UserRolesReplaced, "id")]
    public async Task<IReadOnlyList<RoleBriefOutputDto>> ReplaceRolesAsync(
        Guid id,
        [FromBody] UpdateUserRolesInputDto input,
        CancellationToken cancellationToken)
    {
        return await userAppService.ReplaceRolesAsync(id, input, cancellationToken);
    }

    /// <summary>批量启用用户（需要用户更新权限）。</summary>
    /// <remarks>
    /// 批量端点的注解不带目标：授权被拒时记一条目标为 "-" 的失败；逐条的拒绝由应用服务按目标各记一条。
    /// </remarks>
    [HttpPost("batch/enable")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserEnabled)]
    public async Task<BatchOperationOutputDto> BatchEnableAsync(
        [FromBody] BatchUsersInputDto input,
        CancellationToken cancellationToken)
    {
        return await userAppService.BatchEnableAsync(input, cancellationToken);
    }

    /// <summary>批量禁用用户（需要用户更新权限）。</summary>
    [HttpPost("batch/disable")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserDisabled)]
    public async Task<BatchOperationOutputDto> BatchDisableAsync(
        [FromBody] BatchUsersInputDto input,
        CancellationToken cancellationToken)
    {
        return await userAppService.BatchDisableAsync(input, cancellationToken);
    }
#if (LocalIdentity)

    /// <summary>批量解除用户的登录锁定（需要用户更新权限）。</summary>
    [HttpPost("batch/unlock")]
    [Authorize(Policy = PermissionConstant.Users.Update)]
    [OperationRecordAction(OperationRecordActions.UserUnlocked)]
    public async Task<BatchOperationOutputDto> BatchUnlockAsync(
        [FromBody] BatchUsersInputDto input,
        CancellationToken cancellationToken)
    {
        return await userAppService.BatchUnlockAsync(input, cancellationToken);
    }
#endif

    /// <summary>批量为用户追加角色（需要角色分配权限）。</summary>
    [HttpPost("batch/assign-roles")]
    [Authorize(Policy = PermissionConstant.Users.ManageRoles)]
    [OperationRecordAction(OperationRecordActions.UserRolesReplaced)]
    public async Task<BatchOperationOutputDto> BatchAssignRolesAsync(
        [FromBody] BatchAssignUserRolesInputDto input,
        CancellationToken cancellationToken)
    {
        return await userAppService.BatchAssignRolesAsync(input, cancellationToken);
    }
}
