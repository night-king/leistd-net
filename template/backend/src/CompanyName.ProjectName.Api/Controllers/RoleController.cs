using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Roles.AppServices;
using CompanyName.ProjectName.Application.Roles.Dtos;
using Leistd.OperationRecords.AspNetCore.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>角色管理控制器。</summary>
[Authorize]
[Route("api/v1/roles")]
public sealed class RoleController(IRoleAppService roleAppService) : BaseController
{
    /// <summary>获取角色列表（需要角色查看权限）。</summary>
    [HttpGet]
    [Authorize(Policy = PermissionConstant.Roles.Default)]
    public async Task<PagedResult<RoleOutputDto>> GetPagedListAsync(
        [FromQuery] GetRolePagedInputDto input,
        CancellationToken cancellationToken)
    {
        return await roleAppService.GetPagedListAsync(input, cancellationToken);
    }

    /// <summary>获取全部角色简要信息，供用户角色分配选择使用（需要角色分配权限）。</summary>
    [HttpGet("options")]
    [Authorize(Policy = PermissionConstant.Users.ManageRoles)]
    public async Task<IReadOnlyList<RoleBriefOutputDto>> GetOptionsAsync(CancellationToken cancellationToken)
    {
        return await roleAppService.GetAllAsync(cancellationToken);
    }

    /// <summary>获取角色详情（需要角色查看权限）。</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = PermissionConstant.Roles.Default)]
    public async Task<RoleOutputDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await roleAppService.GetAsync(id, cancellationToken);
    }

    /// <summary>创建角色（需要角色创建权限）。</summary>
    [HttpPost]
    [Authorize(Policy = PermissionConstant.Roles.Create)]
    // 创建类端点还没有目标标识，省略路由键，被拒记录的目标记为 "-"
    [OperationRecordAction(OperationRecordActions.RoleCreated)]
    public async Task<RoleOutputDto> CreateAsync(
        [FromBody] CreateRoleInputDto input,
        CancellationToken cancellationToken)
    {
        return await roleAppService.CreateAsync(input, cancellationToken);
    }

    /// <summary>更新角色（需要角色更新权限）。</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = PermissionConstant.Roles.Update)]
    [OperationRecordAction(OperationRecordActions.RoleUpdated, "id")]
    public async Task<RoleOutputDto> UpdateAsync(
        Guid id,
        [FromBody] UpdateRoleInputDto input,
        CancellationToken cancellationToken)
    {
        return await roleAppService.UpdateAsync(id, input, cancellationToken);
    }

    /// <summary>删除角色（需要角色删除权限）。</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = PermissionConstant.Roles.Delete)]
    // 目标标识取路由上的 id，与成功路径写下的值逐字一致，按目标检索才查得全
    [OperationRecordAction(OperationRecordActions.RoleDeleted, "id")]
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await roleAppService.DeleteAsync(id, cancellationToken);
    }
}
