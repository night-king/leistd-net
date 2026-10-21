#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Domain.Auth.Entities;
using Mapster;

namespace CompanyName.ProjectName.Application.Auth.Mappings;

/// <summary>认证模块映射配置。</summary>
public class AuthMappings : IRegister
{
    /// <summary>MapContext 参数名：发起请求的这台设备的会话 Id，用来标出"当前设备"。</summary>
    public const string CurrentSessionIdKey = "CurrentSessionId";

    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<UserSession, UserSessionOutputDto>()
            .Map(dest => dest.IsCurrent, src => IsCurrentSession(src.Id));
#if (ExternalLogin)

        config.NewConfig<ExternalLoginConnection, ExternalLoginLinkOutputDto>()
            .Map(dest => dest.ProviderAccountLabel, src => src.Profile.AccountLabel)
            .Map(dest => dest.ProviderEmail, src => src.Profile.Email);
#endif
    }

    private static bool IsCurrentSession(Guid sessionId) =>
        MapContext.Current?.Parameters.TryGetValue(CurrentSessionIdKey, out var value) == true &&
        value is Guid current &&
        current == sessionId;
}
#endif
