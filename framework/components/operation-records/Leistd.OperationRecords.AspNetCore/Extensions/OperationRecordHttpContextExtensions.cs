using Leistd.ExceptionHandling;
using Leistd.OperationRecords.Errors;
using Leistd.Security.Claims;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.AspNetCore.Attributes;
using Microsoft.AspNetCore.Authorization;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.OperationRecords.AspNetCore.Extensions;

/// <summary>按端点上的 <see cref="OperationRecordActionAttribute"/>，在授权阶段的拒绝或授权之后的业务拒绝上补记一条失败的操作记录。</summary>
/// <remarks>
/// <para>框架不占用宿主的处置入口：被拒路径在宿主的 <c>IAuthorizationMiddlewareResultHandler</c> 里调用，
/// 业务拒绝在宿主自己的中间件里调用。</para>
/// <para>主要服务于组件映射的端点；宿主自己的应用服务可在拒绝处直接调用 <see cref="IOperationRecorder.RecordFailedAsync"/>。</para>
/// </remarks>
public static class OperationRecordHttpContextExtensions
{
    // 端点上找不到策略名时的授权依据占位
    private const string UnknownAuthorizationBasis = "-";

    // 没有目标标识时的占位；与上面同值但概念不同，不共用常量
    private const string NoTargetId = "-";

    /// <summary>若当前请求是被拒的写操作且端点声明了动作码，记录一条失败记录；否则什么都不做。</summary>
    /// <remarks>
    /// <para>端点打了 <see cref="OperationRecordActionAttribute"/> 就记，不再按 HTTP 方法等启发式筛选。</para>
    /// <para>匿名请求一律不记，避免审计表成为无需凭据的写入面；任一身份已认证即不算匿名，
    /// 与 <c>DenyAnonymousAuthorizationRequirement</c> 一致。</para>
    /// <para>授权依据取实际未通过的策略：叠了多个具名策略时逐个重新评估，取最后声明的未通过者；
    /// 都评估通过（策略依赖请求期状态而前后不一致）时记为 <c>-</c>。</para>
    /// <para>调用方应在确认授权结果为 Forbidden 之后调用本方法。</para>
    /// </remarks>
    /// <param name="context">当前请求上下文。</param>
    public static Task RecordDeniedOperationAsync(this HttpContext context)
        => context.RecordDeniedOperationAsync(OperationFailure.None);

    /// <inheritdoc cref="RecordDeniedOperationAsync(HttpContext)"/>
    /// <param name="context">当前请求上下文。</param>
    /// <param name="failure">
    /// 失败原因。<see cref="OperationFailure.None"/> 时记通用的被拒码；
    /// 只给了 <c>Detail</c>（<c>OperationFailure.FromDetail</c>）也算调用方给过原因，原样保留。
    /// </param>
    public static async Task RecordDeniedOperationAsync(this HttpContext context, OperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.User.HasAuthenticatedIdentity())
        {
            return;
        }

        var endpoint = context.GetEndpoint();
        var declared = endpoint?.Metadata.GetMetadata<OperationRecordActionAttribute>();
        if (endpoint is null || declared is null)
        {
            return;
        }

        var authorizationBasis = await ResolveFailedPolicyAsync(context, endpoint) ?? UnknownAuthorizationBasis;

        var recorder = context.RequestServices.GetRequiredService<IOperationRecorder>();
        // 目标只带标识、不带名字：调用方无权访问该目标，回填名字会把它写进调用方可读的记录。
        // 默认带上通用的被拒码，便于按原因聚合；宿主要附业务参数时自己传 OperationFailure.FromCode(code, data)
        await recorder.RecordFailedAsync(
            declared.Action,
            OperationTarget.For(ResolveTargetId(context.Request.RouteValues, endpoint, declared)),
            authorizationBasis,
            // 按 IsEmpty 判：FromDetail 给出的原因没有码，按 Code 判会覆盖调用方传入的 Detail
            failure.IsEmpty
                ? OperationFailure.FromCode(OperationFailureCodes.Forbidden)
                : failure);
    }

    /// <summary>若当前请求的端点声明了动作码，在授权通过之后的业务拒绝上记录一条失败记录；否则什么都不做。</summary>
    /// <remarks>
    /// <para>在宿主紧接 <c>UseAuthorization()</c> 的中间件里调用：捕获下游业务异常，记录后原样重抛。
    /// 此处授权已通过，且请求仍在租户作用域内。不要在 <c>IExceptionHandler</c> 里调用：那时租户作用域已退出，
    /// 也分不清是否为授权之后的拒绝。</para>
    /// <para>与被拒路径相同：端点打了 <see cref="OperationRecordActionAttribute"/> 才记，匿名请求不记，
    /// 目标标识按 <see cref="OperationRecordActionAttribute.TargetRouteKeys"/> 解析。授权依据取端点最后声明的具名策略，
    /// 不重新评估；没有具名策略时记 <c>-</c>。</para>
    /// <para>本次请求里同一动作码与目标已记过（如应用服务在拒绝处自己调用了 <see cref="IOperationRecorder.RecordFailedAsync"/>）时跳过；
    /// 推不出目标时只按动作码判。因此注解声明的目标须与应用服务记录的目标逐字一致。</para>
    /// </remarks>
    /// <param name="context">当前请求上下文。</param>
    /// <param name="failure">
    /// 失败原因，不能为空。拒绝来自 <see cref="BusinessException"/> 时传
    /// <c>OperationFailure.FromCode(exception.Code, exception.LocalizationData)</c>，不要带入 <c>Exception.Data</c> 与异常文本。
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="failure"/> 为空。</exception>
    public static async Task RecordFailedOperationAsync(this HttpContext context, OperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (failure.IsEmpty)
        {
            throw new ArgumentException("A failed operation needs a reason: pass a code or a detail.", nameof(failure));
        }

        if (!context.User.HasAuthenticatedIdentity())
        {
            return;
        }

        var endpoint = context.GetEndpoint();
        var declared = endpoint?.Metadata.GetMetadata<OperationRecordActionAttribute>();
        if (endpoint is null || declared is null)
        {
            return;
        }

        // 应用服务已记过同一动作与目标时不再补记。推不出目标时传 null 只按动作码判：
        // 按 '-' 比较会与应用服务记下的真实目标永不相等，一次失败记成两条。
        var targetId = ResolveTargetId(context.Request.RouteValues, endpoint, declared);
        var recordedFailures = context.RequestServices.GetRequiredService<RecordedFailureTracker>();
        if (recordedFailures.AlreadyRecorded(
                declared.Action,
                targetId == NoTargetId ? null : targetId))
        {
            return;
        }

        var recorder = context.RequestServices.GetRequiredService<IOperationRecorder>();
        await recorder.RecordFailedAsync(
            declared.Action,
            OperationTarget.For(targetId),
            ResolveDeclaredPolicy(endpoint) ?? UnknownAuthorizationBasis,
            failure);
    }

    // 最后声明的具名策略。类级 [Authorize] 用默认策略、Policy 为空，不参与。
    private static string? ResolveDeclaredPolicy(Endpoint endpoint)
        => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .LastOrDefault(name => !string.IsNullOrEmpty(name));

    // 实际未通过的具名策略：叠加策略时只取书写顺序最后一个会记错。类级 [Authorize] 的 Policy 为空，不参与。
    private static async Task<string?> ResolveFailedPolicyAsync(HttpContext context, Endpoint endpoint)
    {
        var policyNames = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .OfType<string>()
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (policyNames.Count <= 1)
        {
            return policyNames.SingleOrDefault();
        }

        var policyProvider = context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorizationService = context.RequestServices.GetRequiredService<IAuthorizationService>();

        // 从最具体（最后声明，通常是动作级）的往前找；资源与授权中间件一致，传 HttpContext。
        for (var index = policyNames.Count - 1; index >= 0; index--)
        {
            var policy = await policyProvider.GetPolicyAsync(policyNames[index]);
            if (policy is not null
                && !(await authorizationService.AuthorizeAsync(context.User, context, policy)).Succeeded)
            {
                return policyNames[index];
            }
        }

        return null;
    }

    // 按声明顺序取路由值并以 '/' 连接；任何一段缺失就整体记为 '-'，不记半截标识。
    private static string ResolveTargetId(
        RouteValueDictionary routeValues, Endpoint endpoint, OperationRecordActionAttribute declared)
    {
        if (declared.TargetRouteKeys.Count == 0)
        {
            return NoTargetId;
        }

        var segments = new List<string>(declared.TargetRouteKeys.Count);
        foreach (var key in declared.TargetRouteKeys)
        {
            if (!routeValues.TryGetValue(key, out var value)
                || NormalizeRouteValue(endpoint, key, value) is not { Length: > 0 } routeValue)
            {
                return NoTargetId;
            }

            segments.Add(routeValue);
        }

        return declared.TargetIdPrefix + string.Join('/', segments);
    }

    // 有类型依据的 Guid 段归一为小写 D，与 OperationTarget.For(Guid) 一致，去重与按目标检索才对得上。
    // 没有依据的字符串保留原文：32 位十六进制或 Guid 外观的字符串主键不能被改写。
    // 路由值本身是 Guid 对象时 ToString() 已是小写 D。
    private static string? NormalizeRouteValue(Endpoint endpoint, string key, object? value)
    {
        var text = value?.ToString();
        return text is not null && IsGuidRouteValue(endpoint, key) && Guid.TryParse(text, out var parsed)
            ? parsed.ToString()
            : text;
    }

    // 类型依据：路由参数带 guid 约束，或从路由绑定到该键的动作参数是 Guid / Guid?。
    // 同名但显式取自查询、请求头、请求体等来源的参数不算依据，否则会改写字符串路由键。
    private static bool IsGuidRouteValue(Endpoint endpoint, string key)
    {
        var pattern = (endpoint as RouteEndpoint)?.RoutePattern;
        if (pattern?.GetParameter(key)?.ParameterPolicies.Any(IsGuidConstraint) == true)
        {
            return true;
        }

        // 未显式指定来源的参数，与官方推断一致：名字是路由模板参数才算路由绑定
        var isPatternParameter = pattern?.GetParameter(key) is not null;

        // MVC：按模型绑定名（含 [FromRoute(Name = ...)] 别名）匹配；路由键不区分大小写。
        // [ApiController] 的来源推断在构建动作描述符前完成，路由参数此时已是 Path。
        if (endpoint.Metadata.GetMetadata<ActionDescriptor>() is { } action)
        {
            return action.Parameters.Any(parameter =>
                IsGuid(parameter.ParameterType)
                && string.Equals(parameter.BindingInfo?.BinderModelName ?? parameter.Name, key, StringComparison.OrdinalIgnoreCase)
                && IsRouteBindingSource(parameter.BindingInfo?.BindingSource, isPatternParameter));
        }

        // Minimal API：处理方法随端点元数据登记，按参数名或 [FromRoute(Name = ...)] 别名匹配
        return endpoint.Metadata.GetMetadata<MethodInfo>()?.GetParameters().Any(parameter =>
            IsGuid(parameter.ParameterType)
            && IsMinimalRouteParameter(parameter, key, isPatternParameter)) == true;
    }

    // Path 明确来自路由；未指定（null）或 ModelBinding（不限来源）只在键是模板参数时算；其余来源都不算
    private static bool IsRouteBindingSource(BindingSource? source, bool isPatternParameter)
        => source == BindingSource.Path
            || (isPatternParameter && (source is null || source == BindingSource.ModelBinding));

    private static bool IsMinimalRouteParameter(ParameterInfo parameter, string key, bool isPatternParameter)
    {
        var attributes = parameter.GetCustomAttributes(inherit: true);
        if (attributes.OfType<IFromRouteMetadata>().FirstOrDefault() is { } fromRoute)
        {
            return string.Equals(fromRoute.Name ?? parameter.Name, key, StringComparison.OrdinalIgnoreCase);
        }

        return isPatternParameter
            && !attributes.Any(IsExplicitNonRouteSource)
            && string.Equals(parameter.Name, key, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitNonRouteSource(object attribute)
        => attribute is IFromQueryMetadata or IFromHeaderMetadata or IFromBodyMetadata
            or IFromServiceMetadata or IFromFormMetadata or AsParametersAttribute or FromKeyedServicesAttribute;

    private static bool IsGuidConstraint(RoutePatternParameterPolicyReference policy)
        => policy.ParameterPolicy is GuidRouteConstraint
            || string.Equals(policy.Content, "guid", StringComparison.OrdinalIgnoreCase);

    private static bool IsGuid(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(Guid);
}
