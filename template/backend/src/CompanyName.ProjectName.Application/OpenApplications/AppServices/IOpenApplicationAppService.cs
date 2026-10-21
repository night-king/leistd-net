#if (LocalIdentity)
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.Application.OpenApplications.AppServices;

public interface IOpenApplicationAppService : IAppService
{
    Task<PagedResult<OpenApplicationOutputDto>> GetPagedListAsync(
        GetOpenApplicationPagedInputDto input,
        CancellationToken cancellationToken = default);

    Task<OpenApplicationOutputDto> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<OpenApplicationOutputDto> CreateAsync(
        CreateOpenApplicationInputDto input,
        CancellationToken cancellationToken = default);

    Task<OpenApplicationOutputDto> UpdateAsync(
        string id,
        UpdateOpenApplicationInputDto input,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<ResetOpenApplicationSecretOutputDto> ResetSecretAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>可授予开放应用的 scope（本服务能签发的全部 scope，含配置的下游 API）。</summary>
    IReadOnlyList<OpenApplicationScopeOutputDto> GetScopes();
}
#endif
