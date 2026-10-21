using CompanyName.ProjectName.Application.Roles.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.Application.Roles.AppServices;

/// <summary>角色应用服务接口。</summary>
public interface IRoleAppService : IAppService
{
    /// <summary>获取角色列表（分页）。</summary>
    Task<PagedResult<RoleOutputDto>> GetPagedListAsync(
        GetRolePagedInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>获取全部角色的简要信息，供用户角色分配等选择场景使用。</summary>
    Task<IReadOnlyList<RoleBriefOutputDto>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>获取角色详情。</summary>
    Task<RoleOutputDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>创建角色。</summary>
    Task<RoleOutputDto> CreateAsync(CreateRoleInputDto input, CancellationToken cancellationToken = default);

    /// <summary>更新角色。</summary>
    Task<RoleOutputDto> UpdateAsync(
        Guid id,
        UpdateRoleInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>删除角色。系统内置角色不可删除；仍有用户关联的角色需先解除关联。</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
