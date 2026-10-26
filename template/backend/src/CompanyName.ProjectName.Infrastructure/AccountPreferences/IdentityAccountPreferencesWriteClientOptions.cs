#if (RemoteTokenAuth)
using Leistd.ServiceClient.Options;

namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>写入偏好的命名客户端配置；与读取客户端共用地址，认证另走 Token Exchange。</summary>
public sealed class IdentityAccountPreferencesWriteClientOptions : ServiceClientOptions;
#endif
