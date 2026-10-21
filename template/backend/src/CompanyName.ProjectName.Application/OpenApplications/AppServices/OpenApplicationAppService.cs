#if (LocalIdentity)
#if (OpenIddictServer)
using CompanyName.ProjectName.Application.OpenApplications.Errors;
#endif
using Leistd.ExceptionHandling;
using System.Security.Cryptography;
using System.Text.Json;
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Shared.Paging;
using CompanyName.ProjectName.Application.Auth.OAuth;
using CompanyName.ProjectName.Application.Auth.Options;
using Leistd.Ddd.Application.AppServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CompanyName.ProjectName.Application.OpenApplications.Mappings;
using Leistd.ObjectMapping.Abstractions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using OpenIddict.Abstractions;
using Leistd.Timing;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.Application.OpenApplications.AppServices;

public class OpenApplicationAppService(
    IOpenIddictApplicationManager applicationManager,
    IOptions<OAuthResourceOptions> oauthOptions,
    IObjectMapper objectMapper,
    IOperationRecorder operationRecorder,
    IClock clock,
    ILogger<OpenApplicationAppService> logger) : BaseAppService, IOpenApplicationAppService
{
    private const string PkceRequirement = "ft:pkce";

    /// <summary>本服务能签发的 scope 与其中仅限机器的那些，都取自 scope 目录（见 <see cref="OAuthScopes"/>）。</summary>
    /// <remarks>
    /// <para>写入时按目录校验 scope 与 audience，并校验交换客户端的权限组合，
    /// 避免保存可登记但无法使用的客户端；不维护完整的官方权限词汇表。</para>
    /// <para>仅限机器的 scope（租户连接回源与迁移控制面）只能发给服务间调用的机器客户端：
    /// 授出去就没有回收窗口，创建时挡住比事后审计便宜；资源端策略另有一道。</para>
    /// </remarks>
    private IReadOnlyList<OAuthScope> ScopeCatalog => OAuthScopes.All(oauthOptions.Value);

    /// <summary>代表自然人的授权流：与内部控制面 scope 互斥。</summary>
    /// <remarks>
    /// 同一个客户端既能拿 client credentials 机器令牌、又能走用户授权流时，
    /// 机器 scope 会随用户令牌一起签发——那个令牌的 <c>sub</c> 是用户 GUID，
    /// 过不了资源端的机器主体判定，但客户端配置本身已经把两种信任级别混在一起了，
    /// 这是配置错误而不是运行期问题，应该在写入时就拒绝。
    /// </remarks>
    private static readonly string[] HumanGrantPermissions =
    [
        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
        OpenIddictConstants.Permissions.GrantTypes.Implicit,
        OpenIddictConstants.Permissions.GrantTypes.Password,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.GrantTypes.DeviceCode
    ];


    public async Task<PagedResult<OpenApplicationOutputDto>> GetPagedListAsync(
        GetOpenApplicationPagedInputDto input,
        CancellationToken cancellationToken = default)
    {
        var intermediateItems = new List<OpenApplicationQueryItem>();
        await foreach (var app in applicationManager.ListAsync(count: null, offset: null, cancellationToken))
        {
            intermediateItems.Add(new OpenApplicationQueryItem
            {
                Application = app,
                ClientId = await applicationManager.GetClientIdAsync(app, cancellationToken) ?? string.Empty,
                DisplayName = await applicationManager.GetDisplayNameAsync(app, cancellationToken),
                ApplicationType = await applicationManager.GetApplicationTypeAsync(app, cancellationToken),
                ClientType = await applicationManager.GetClientTypeAsync(app, cancellationToken),
                CreationTime = OpenApplicationMappings.ReadCreationTime(
                    await applicationManager.GetPropertiesAsync(app, cancellationToken))
            });
        }

        var query = intermediateItems.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(input.Keyword))
        {
            var keyword = input.Keyword.Trim();
            query = query.Where(item =>
                item.ClientId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (item.DisplayName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(input.ApplicationType))
        {
            query = query.Where(item => item.ApplicationType == input.ApplicationType);
        }

        if (!string.IsNullOrWhiteSpace(input.ClientType))
        {
            query = query.Where(item => item.ClientType == input.ClientType);
        }

        var filteredItems = query.AsQueryable().OrderBySorting(input.Sorting)
            .ThenBy(item => item.ClientId, StringComparer.Ordinal).ToList();
        var totalCount = filteredItems.Count;
        var pagedItems = filteredItems
            .Skip(input.Offset)
            .Take(input.Limit)
            .ToList();

        var outputItems = new List<OpenApplicationOutputDto>();
        foreach (var item in pagedItems)
        {
            outputItems.Add(await MapToOutputAsync(item.Application, cancellationToken));
        }

        return new PagedResult<OpenApplicationOutputDto>(totalCount, outputItems);
    }

    public async Task<OpenApplicationOutputDto> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var application = await FindRequiredAsync(id, cancellationToken);
        return await MapToOutputAsync(application, cancellationToken);
    }

    public async Task<OpenApplicationOutputDto> CreateAsync(
        CreateOpenApplicationInputDto input,
        CancellationToken cancellationToken = default)
    {
        var clientId = input.ClientId.Trim();
        if (await applicationManager.FindByClientIdAsync(clientId, cancellationToken) != null)
        {
            throw new BusinessException(OpenAppErrorCodes.ClientIdTaken, $"Client ID already exists: {clientId}")
                .WithData("ClientId", clientId);
        }

        ValidateApplication(input.ApplicationType, input.ClientType, input.Requirements, input.Permissions);

        // Confidential 客户端：自动生成 Secret
        string? generatedSecret = null;
        if (input.ClientType == OpenIddictConstants.ClientTypes.Confidential)
        {
            generatedSecret = GenerateClientSecret();
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim(),
            ApplicationType = input.ApplicationType,
            ClientType = input.ClientType,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            ClientSecret = generatedSecret
        };

        ApplyCollections(
            descriptor,
            input.RedirectUris,
            input.PostLogoutRedirectUris,
            input.Permissions,
            input.Requirements);
        descriptor.Properties[OpenApplicationMappings.CreationTimePropertyName] = JsonSerializer.SerializeToElement(clock.Now);
        descriptor.Settings[OpenApplicationSettings.SessionBound] = OpenApplicationSettings.Format(input.SessionBound!.Value);

        try
        {
            var application = await applicationManager.CreateAsync(descriptor, cancellationToken);
            logger.LogInformation("Open application created (ClientId: {ClientId})", clientId);

            var output = await MapToOutputAsync(application, cancellationToken);
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.OpenApplicationCreated,
                OperationTarget.For(output.Id, output.DisplayName ?? output.ClientId),
                PermissionConstant.OpenApplications.Create,
                cancellationToken);

            // 创建时返回生成的 Secret（仅此一次）
            if (generatedSecret != null)
            {
                return output with { ClientSecret = generatedSecret };
            }

            return output;
        }
        catch (OpenIddictExceptions.ValidationException ex)
        {
            logger.LogWarning(ex, "OpenIddict validation failed (ClientId: {ClientId})", clientId);
            throw new BusinessException(OpenAppErrorCodes.CreateFailed, "The open application could not be created.", ex);
        }
    }

    public async Task<OpenApplicationOutputDto> UpdateAsync(
        string id,
        UpdateOpenApplicationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ValidateApplication(input.ApplicationType, input.ClientType, input.Requirements, input.Permissions);

        var application = await FindRequiredAsync(id, cancellationToken);
        var descriptor = new OpenIddictApplicationDescriptor();
        await applicationManager.PopulateAsync(descriptor, application, cancellationToken);

        descriptor.DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim();
        descriptor.ApplicationType = input.ApplicationType;
        descriptor.ClientType = input.ClientType;
        descriptor.ConsentType = OpenIddictConstants.ConsentTypes.Implicit;
        if (input.ClientType == OpenIddictConstants.ClientTypes.Public)
        {
            descriptor.ClientSecret = null;
        }

        ApplyCollections(
            descriptor,
            input.RedirectUris,
            input.PostLogoutRedirectUris,
            input.Permissions,
            input.Requirements);
        // 只改模板自有的键，其余 Settings 原样保留
        descriptor.Settings[OpenApplicationSettings.SessionBound] = OpenApplicationSettings.Format(input.SessionBound!.Value);

        await applicationManager.UpdateAsync(application, descriptor, cancellationToken);
        logger.LogInformation("Open application updated (ID: {Id})", id);
        // 管理器已把描述符写回手里的实例，用它构造输出，不再按 Id 回查
        var output = await MapToOutputAsync(application, cancellationToken);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.OpenApplicationUpdated,
            OperationTarget.For(id, output.DisplayName ?? output.ClientId),
            PermissionConstant.OpenApplications.Update,
            cancellationToken);
        return output;
    }

    /// <remarks>删除幂等：不存在时直接成功，不写操作记录——什么也没删。</remarks>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var application = await applicationManager.FindByIdAsync(id, cancellationToken);
        if (application == null)
            return;

        // 名字在删除前取：删完再查什么都查不到，而审计要回答的正是"当时删掉的是哪一个"
        var name = await GetTargetNameAsync(application, cancellationToken);
        await applicationManager.DeleteAsync(application, cancellationToken);
        logger.LogInformation("Open application deleted (ID: {Id})", id);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.OpenApplicationDeleted,
            OperationTarget.For(id, name),
            PermissionConstant.OpenApplications.Delete,
            cancellationToken);
    }

    public async Task<ResetOpenApplicationSecretOutputDto> ResetSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        var application = await FindRequiredAsync(id, cancellationToken);
        var clientType = await applicationManager.GetClientTypeAsync(application, cancellationToken);
        if (clientType != OpenIddictConstants.ClientTypes.Confidential)
        {
            throw new BusinessException(OpenAppErrorCodes.SecretResetConfidentialOnly, "Only confidential clients can reset their secret.");
        }

        var clientSecret = GenerateClientSecret();
        await applicationManager.UpdateAsync(application, clientSecret, cancellationToken);
        logger.LogInformation("Open application secret reset (ID: {Id})", id);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.OpenApplicationSecretReset,
            OperationTarget.For(id, await GetTargetNameAsync(application, cancellationToken)),
            PermissionConstant.OpenApplications.ResetSecret,
            cancellationToken);
        return new ResetOpenApplicationSecretOutputDto { ClientSecret = clientSecret };
    }

    private async Task<object> FindRequiredAsync(string id, CancellationToken cancellationToken)
    {
        var application = await applicationManager.FindByIdAsync(id, cancellationToken);
        if (application == null)
        {
            throw new BusinessException(OpenAppErrorCodes.NotFound, $"Open application not found: {id}")
                .WithData("Id", id);
        }

        return application;
    }

    /// <summary>操作记录的目标名：显示名，退到 Client ID；四个写操作同一套取值规则。</summary>
    private async Task<string?> GetTargetNameAsync(object application, CancellationToken cancellationToken) =>
        await applicationManager.GetDisplayNameAsync(application, cancellationToken)
        ?? await applicationManager.GetClientIdAsync(application, cancellationToken);

    /// <remarks><c>PopulateAsync</c> 一次填满除 <c>Id</c> 以外的全部字段，<c>Id</c> 经映射上下文传入。</remarks>
    private async Task<OpenApplicationOutputDto> MapToOutputAsync(object application, CancellationToken cancellationToken)
    {
        var descriptor = new OpenIddictApplicationDescriptor();
        await applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        var id = await applicationManager.GetIdAsync(application, cancellationToken);

        return objectMapper.Map<OpenIddictApplicationDescriptor, OpenApplicationOutputDto>(
            descriptor,
            new Dictionary<string, object> { [OpenApplicationMappings.IdKey] = id ?? string.Empty });
    }

    /// <inheritdoc />
    public IReadOnlyList<OpenApplicationScopeOutputDto> GetScopes() =>
        ScopeCatalog
            .Select(scope => new OpenApplicationScopeOutputDto
            {
                Name = scope.Name,
                DisplayName = scope.DisplayName,
                Audience = !scope.MachineOnly && scope.Resources.Count == 1 ? scope.Resources[0] : null,
                MachineOnly = scope.MachineOnly
            })
            .ToList();

    /// <remarks>
    /// 取值范围与回调地址格式由入参 DTO 校验；这里只判跨字段组合与依赖 scope 目录的规则。
    /// Secret 由后端自动生成，不从前端传入，无需校验。
    /// </remarks>
    private void ValidateApplication(
        string applicationType,
        string clientType,
        IReadOnlyCollection<string> requirements,
        IReadOnlyCollection<string> permissions)
    {
        if ((applicationType == OpenIddictConstants.ApplicationTypes.Native || clientType == OpenIddictConstants.ClientTypes.Public) &&
            !requirements.Contains(PkceRequirement))
        {
            throw new BusinessException(OpenAppErrorCodes.PkceRequired, "PKCE must be enabled for native/public clients.");
        }

        foreach (var permission in permissions.Where(x =>
                     x.StartsWith(OpenIddictConstants.Permissions.Prefixes.Scope, StringComparison.Ordinal)))
        {
            var scopeName = permission[OpenIddictConstants.Permissions.Prefixes.Scope.Length..];
            if (ScopeCatalog.Any(scope => scope.Name == scopeName))
                continue;

            throw new BusinessException(OpenAppErrorCodes.ScopeUnsupported, $"Unsupported scope permission: {permission}")
                .WithData("Scope", permission);
        }

        ValidateMachineOnlyScopes(clientType, permissions);
        if (permissions.Contains(OpenIddictConstants.Permissions.GrantTypes.TokenExchange) &&
            (clientType != OpenIddictConstants.ClientTypes.Confidential ||
             !permissions.Contains(OpenIddictConstants.Permissions.Endpoints.Token) ||
             permissions.Any(HumanGrantPermissions.Contains)))
            throw new BusinessException(OpenAppErrorCodes.ExchangeClientInvalid,
                "Token Exchange requires a confidential service client with token permission and no user-facing grants.");
        foreach (var permission in permissions.Where(permission => permission.StartsWith(OpenIddictConstants.Permissions.Prefixes.Audience, StringComparison.Ordinal)))
        {
            var audience = permission[OpenIddictConstants.Permissions.Prefixes.Audience.Length..];
            if (!ScopeCatalog.SelectMany(scope => scope.Resources).Contains(audience))
                throw new BusinessException(OpenAppErrorCodes.AudienceUnsupported, $"Unsupported audience: {audience}").WithData("Audience", audience);
        }
    }


    /// <summary>内部控制面 scope 的四条组合约束。</summary>
    /// <remarks>
    /// 内部控制面 scope 只允许 confidential 客户端，并要求 <c>client_credentials</c>
    /// 与 <c>ept:token</c> 权限；缺少任一项都无法从令牌端点取得该 scope。
    /// 同一客户端不得启用用户授权流，避免混合机器与自然人的信任边界。
    /// <c>applicationType=service</c> 仅为分类信息，不作为安全断言。
    /// </remarks>
    private void ValidateMachineOnlyScopes(
        string clientType,
        IReadOnlyCollection<string> permissions)
    {
        var machineScopes = ScopeCatalog
            .Where(scope => scope.MachineOnly)
            .Select(scope => OpenIddictConstants.Permissions.Prefixes.Scope + scope.Name)
            .Where(permissions.Contains)
            .ToList();
        if (machineScopes.Count == 0)
        {
            return;
        }

        var scopeList = string.Join(", ", machineScopes);

        if (clientType != OpenIddictConstants.ClientTypes.Confidential)
        {
            throw new BusinessException(OpenAppErrorCodes.MachineScopeRequiresConfidential,
                    $"Machine-only scopes ({scopeList}) require a confidential client.")
                .WithData("Scopes", scopeList);
        }

        if (!permissions.Contains(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials))
        {
            throw new BusinessException(OpenAppErrorCodes.MachineScopeRequiresClientCredentials,
                    $"Machine-only scopes ({scopeList}) require the client_credentials grant type.")
                .WithData("Scopes", scopeList);
        }

        if (!permissions.Contains(OpenIddictConstants.Permissions.Endpoints.Token))
        {
            throw new BusinessException(OpenAppErrorCodes.MachineScopeRequiresTokenEndpoint,
                    $"Machine-only scopes ({scopeList}) require the token endpoint permission.")
                .WithData("Scopes", scopeList);
        }

        var humanGrants = permissions.Where(HumanGrantPermissions.Contains).ToList();
        if (humanGrants.Count > 0)
        {
            throw new BusinessException(OpenAppErrorCodes.MachineScopeRejectsUserGrants,
                $"Machine-only scopes ({scopeList}) cannot be combined with user-facing grant " +
                $"types ({string.Join(", ", humanGrants)}).")
                .WithData("Scopes", scopeList)
                .WithData("Grants", string.Join(", ", humanGrants));
        }
    }

    private static void ApplyCollections(
        OpenIddictApplicationDescriptor descriptor,
        IEnumerable<string> redirectUris,
        IEnumerable<string> postLogoutRedirectUris,
        IEnumerable<string> permissions,
        IEnumerable<string> requirements)
    {
        descriptor.RedirectUris.Clear();
        foreach (var uri in redirectUris.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }

        descriptor.PostLogoutRedirectUris.Clear();
        foreach (var uri in postLogoutRedirectUris.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }

        descriptor.Permissions.Clear();
        foreach (var permission in permissions.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            descriptor.Permissions.Add(permission);
        }

        descriptor.Requirements.Clear();
        foreach (var requirement in requirements.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            descriptor.Requirements.Add(requirement);
        }
    }

    private static string GenerateClientSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

}
#endif
