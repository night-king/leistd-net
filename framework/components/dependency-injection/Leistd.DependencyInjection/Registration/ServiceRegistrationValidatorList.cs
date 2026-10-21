using Microsoft.Extensions.DependencyInjection;

namespace Leistd.DependencyInjection.Registration;

/// <summary>服务集合校验器列表；校验器先于描述符回调执行，可检查完整 <see cref="IServiceCollection"/>。</summary>
public class ServiceRegistrationValidatorList : List<Action<IServiceCollection>>
{
}
