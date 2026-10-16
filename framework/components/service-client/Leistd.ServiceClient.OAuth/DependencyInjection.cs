using Leistd.ServiceClient.Abstractions;
using Leistd.ServiceClient.OAuth.Handlers;
using Leistd.ServiceClient.OAuth.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenIddict.Client;

namespace Leistd.ServiceClient.OAuth;

/// <summary>官方 OpenIddict.Client 服务认证注册入口。</summary>
public static class DependencyInjection
{
    internal const string RegistrationId = "Leistd.ServiceClient";

    /// <summary>绑定工作负载身份，启用官方发现与客户端认证；令牌不进入官方数据库存储。</summary>
    /// <remarks>
    /// 本服务只有一个工作负载身份：以相同 <paramref name="configSectionPath"/> 重复调用只追加 <paramref name="configure"/>，
    /// 不重复登记验证器与官方客户端处理器；换用另一配置节时抛出 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public static IServiceCollection AddServiceAuthentication(this IServiceCollection services,
        Action<ServiceAuthenticationOptions>? configure = null,
        string configSectionPath = ServiceAuthenticationOptions.SectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);
        var options = services.AddOptions<ServiceAuthenticationOptions>();
        if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<ServiceAuthenticationOptionsValidator>().FirstOrDefault()
            is { } registered)
        {
            if (registered.ConfigSectionPath != configSectionPath)
                throw new InvalidOperationException(
                    $"Service authentication is already bound to '{registered.ConfigSectionPath}'; it cannot also bind '{configSectionPath}'.");
            if (configure is not null) options.Configure(configure);
            return services;
        }

        options.BindConfiguration(configSectionPath);
        if (configure is not null) options.Configure(configure);
        services.AddSingleton<IValidateOptions<ServiceAuthenticationOptions>>(new ServiceAuthenticationOptionsValidator(configSectionPath));
        options.ValidateOnStart();
        services.AddHybridCache();
        services.AddOpenIddict().AddClient(client =>
        {
            client.AllowClientCredentialsFlow().AllowTokenExchangeFlow().DisableTokenStorage();
            client.UseSystemNetHttp();
            // 官方处理器会把远端响应原文（含令牌）写进日志，前置接手解析与状态校验。
            foreach (var descriptor in ResponsePayloadLoggingGuard.Descriptors) client.AddEventHandler(descriptor);
            // 官方发送处理器把调用方取消记成网络故障（6182 Error），换成取消照常抛出的同序处理器。
            foreach (var descriptor in SendCancellationGuard.Replaced) client.RemoveEventHandler(descriptor);
            foreach (var descriptor in SendCancellationGuard.Descriptors) client.AddEventHandler(descriptor);
        });
        services.AddOptions<OpenIddictClientOptions>().Configure<IOptions<ServiceAuthenticationOptions>>((client, identity) =>
        {
            var value = identity.Value;
            if (client.Registrations.Any(registration => registration.RegistrationId == RegistrationId)) return;
            client.Registrations.Add(new OpenIddictClientRegistration
            {
                RegistrationId = RegistrationId, Issuer = new Uri(value.Authority, UriKind.Absolute),
                ClientId = value.ClientId, ClientSecret = value.ClientSecret
            });
        });
        services.TryAddSingleton<TokenCache>();
        return services;
    }

    /// <summary>为命名客户端追加机器认证；默认绑定 Leistd:ServiceClients:{Name}。</summary>
    /// <remarks>
    /// 每个命名客户端只有一种认证方式：以相同配置节重复调用只追加 <paramref name="configure"/>，不重复挂处理器；
    /// 换用另一配置节或该客户端已挂 Token Exchange 时抛出 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public static IHttpClientBuilder AddClientCredentials(this IHttpClientBuilder builder,
        Action<ClientCredentialsOptions>? configure = null, string? configSectionPath = null)
    {
        var path = configSectionPath ?? $"{ServiceClient.DependencyInjection.ConfigurationSectionPrefix}:{builder.Name}";
        var options = builder.Services.AddOptions<ClientCredentialsOptions>(builder.Name);
        if (!RegisterAuthentication(builder, nameof(AddClientCredentials), path))
        {
            if (configure is not null) options.Configure(configure);
            return builder;
        }

        options.BindConfiguration(path);
        if (configure is not null) options.Configure(configure);
        options.ValidateOnStart();
        return builder.AddHttpMessageHandler(provider => new ClientCredentialsDelegatingHandler(builder.Name,
            provider.GetRequiredService<OpenIddictClientService>(), provider.GetRequiredService<TokenCache>(),
            provider.GetRequiredService<IOptions<ServiceAuthenticationOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<ClientCredentialsOptions>>()));
    }

    /// <summary>追加单跳 Token Exchange；没有已验证的用户令牌时拒绝调用，不回退为机器身份。</summary>
    /// <remarks>
    /// 每个命名客户端只有一种认证方式：以相同配置节重复调用只追加 <paramref name="configure"/>，不重复挂处理器与验证器；
    /// 换用另一配置节或该客户端已挂机器认证时抛出 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public static IHttpClientBuilder AddTokenExchange(this IHttpClientBuilder builder,
        Action<TokenExchangeOptions>? configure = null, string? configSectionPath = null)
    {
        var path = configSectionPath ?? $"{ServiceClient.DependencyInjection.ConfigurationSectionPrefix}:{builder.Name}:TokenExchange";
        var options = builder.Services.AddOptions<TokenExchangeOptions>(builder.Name);
        if (!RegisterAuthentication(builder, nameof(AddTokenExchange), path))
        {
            if (configure is not null) options.Configure(configure);
            return builder;
        }

        options.BindConfiguration(path);
        if (configure is not null) options.Configure(configure);
        builder.Services.AddSingleton<IValidateOptions<TokenExchangeOptions>>(new TokenExchangeOptionsValidator(builder.Name, path));
        options.ValidateOnStart();
        return builder.AddHttpMessageHandler(provider => new TokenExchangeDelegatingHandler(builder.Name,
            provider.GetRequiredService<OpenIddictClientService>(), provider.GetRequiredService<TokenCache>(),
            provider.GetRequiredService<IOptions<ServiceAuthenticationOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<TokenExchangeOptions>>(),
            provider.GetRequiredService<IUserAccessTokenAccessor>()));
    }

    private sealed record AuthenticationMode(string Name, string Kind, string ConfigSectionPath);

    // 首次登记返回 true；相同登记的重复调用返回 false，由调用方跳过处理器与验证器。
    private static bool RegisterAuthentication(IHttpClientBuilder builder, string kind, string configSectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);
        var mode = new AuthenticationMode(builder.Name, kind, configSectionPath);
        if (builder.Services.Select(descriptor => descriptor.ImplementationInstance).OfType<AuthenticationMode>()
                .FirstOrDefault(existing => existing.Name == builder.Name) is { } registered)
        {
            if (registered == mode) return false;
            throw new InvalidOperationException(
                $"Service client '{builder.Name}' already has an authentication handler ({registered.Kind}, section " +
                $"'{registered.ConfigSectionPath}'); it cannot also use {kind} with section '{configSectionPath}'.");
        }

        builder.Services.AddSingleton(mode);
        return true;
    }

}
