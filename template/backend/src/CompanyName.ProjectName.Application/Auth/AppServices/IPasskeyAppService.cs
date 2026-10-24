#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Dtos;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>本人的通行密钥：登记、重命名与删除。</summary>
public interface IPasskeyAppService
{
    /// <summary>本人的全部通行密钥。</summary>
    Task<IReadOnlyList<PasskeyOutputDto>> GetListAsync(CancellationToken cancellationToken = default);

    /// <summary>开始登记：确认是本人后生成交给浏览器的创建选项。</summary>
    Task<PasskeyOptionsOutputDto> BeginRegistrationAsync(BeginPasskeyRegistrationInputDto input, CancellationToken cancellationToken = default);

    /// <summary>完成登记：校验浏览器创建的凭据并保存；它是第一个独立因子时一并发放恢复码。</summary>
    Task<PasskeyRegistrationOutputDto> CompleteRegistrationAsync(CompletePasskeyRegistrationInputDto input, CancellationToken cancellationToken = default);

    /// <summary>确认身份：给出用通行密钥确认的断言选项（只列本人登记的），之后把断言作为确认凭证交给要求确认的入口。</summary>
    Task<PasskeyOptionsOutputDto> BeginReauthenticationAsync(CancellationToken cancellationToken = default);

    /// <summary>重命名。</summary>
    Task<PasskeyOutputDto> RenameAsync(Guid id, RenamePasskeyInputDto input, CancellationToken cancellationToken = default);

    /// <summary>删除：会话不够新时带上本人的一种凭证。</summary>
    Task RemoveAsync(Guid id, RemovePasskeyInputDto input, CancellationToken cancellationToken = default);
}
#endif
