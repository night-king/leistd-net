using Leistd.DependencyInjection.Abstractions;

namespace Leistd.DependencyInjection.Registration;

/// <summary>服务描述符回调列表。</summary>
public class ServiceRegistrationActionList : List<Action<IOnServiceRegisteredContext>>
{
}
