#if (RemoteTokenAuth)
using Leistd.ServiceClient.Options;

namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>读取偏好的命名客户端配置；与写入客户端共用 <c>Leistd:ServiceClients:Identity</c> 的地址。</summary>
public sealed class IdentityAccountPreferencesReadClientOptions : ServiceClientOptions;
#endif
