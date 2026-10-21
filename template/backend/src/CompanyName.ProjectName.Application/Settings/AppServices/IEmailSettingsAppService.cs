#if (LocalIdentity)
using CompanyName.ProjectName.Application.Settings.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Settings.AppServices;

/// <summary>发信设置的业务能力：用当前生效的发信参数发一封测试邮件。</summary>
/// <remarks>设置本身的读写由设置组件提供（<c>MapSettings</c>）；这里只放组件不认识的业务动作。</remarks>
public interface IEmailSettingsAppService : IAppService
{
    /// <summary>用当前生效的发信参数发一封测试邮件（只在宿主上下文、需要设置管理权限）。</summary>
    /// <remarks>连接、认证或投递失败时以 400 返回失败原因，管理员据此改参数，而不是去翻服务端日志。</remarks>
    Task SendTestEmailAsync(SendTestEmailInputDto input, CancellationToken cancellationToken = default);
}
#endif
