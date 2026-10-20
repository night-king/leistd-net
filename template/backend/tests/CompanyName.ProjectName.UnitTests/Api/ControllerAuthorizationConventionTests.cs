using System.Reflection;
using CompanyName.ProjectName.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace CompanyName.ProjectName.UnitTests.Api;

/// <summary>控制器授权约定：每个 action 显式声明授权；写操作要么带策略，要么列入有理由的豁免。</summary>
/// <remarks>
/// 只有类级 <c>[Authorize]</c> 的写端点等于"登录即可写"，权限判定只能退回应用服务里手写，
/// 被拒时也拿不到管道的 403。这类回退在评审里不显眼，由本约定在构建后的程序集上拦住。
/// Minimal API 映射的组件端点由各组件的策略选项把关，不在本约定范围内。
/// </remarks>
public class ControllerAuthorizationConventionTests
{
    /// <summary>不带策略的写端点豁免清单：匿名协议端点与本人自助端点，每项写明理由。</summary>
    private static readonly AuthorizationExemption[] Exemptions =
    [
        new(ExemptionKind.AnonymousProtocol, "POST api/v1/auth/session-login", "登录本身，调用时还没有主体"),
        new(ExemptionKind.AnonymousProtocol, "POST api/v1/auth/two-factor", "登录第二步，主体尚未签发"),
        new(ExemptionKind.AnonymousProtocol, "POST api/v1/auth/logout", "注销对无会话的请求同样成功"),
        new(ExemptionKind.AnonymousProtocol, "POST api/v1/auth/send-email-code", "注册前的邮箱验证码"),
        new(ExemptionKind.AnonymousProtocol, "POST api/v1/auth/register", "自助注册"),
        new(ExemptionKind.AnonymousProtocol, "POST api/v1/auth/password-reset/send-code", "忘记口令时发验证码，对任意邮箱同一响应"),
        new(ExemptionKind.AnonymousProtocol, "POST api/v1/auth/password-reset", "凭邮箱验证码设置新口令，不签发会话"),
        new(ExemptionKind.AnonymousProtocol, "POST api/v1/external-auth/{provider}/complete", "外部登录回调完成登录"),
        new(ExemptionKind.AnonymousProtocol, "POST connect/authorize", "OIDC 授权端点，由 OpenIddict 校验请求"),
        new(ExemptionKind.AnonymousProtocol, "POST connect/logout", "OIDC 注销端点"),
        new(ExemptionKind.AnonymousProtocol, "POST connect/token", "OAuth 令牌端点，凭客户端凭据或授权码认证"),
        new(ExemptionKind.SelfService, "POST connect/userinfo", "OIDC userinfo，按访问令牌返回本人资料"),
        new(ExemptionKind.SelfService, "* api/v1/auth/me/**", "本人资料、会话与两步验证"),
        new(ExemptionKind.SelfService, "POST api/v1/auth/change-password", "修改本人密码，需验证当前密码"),
        new(ExemptionKind.SelfService, "POST api/v1/auth/end-impersonation", "结束模拟，回到发起人本人"),
        new(ExemptionKind.SelfService, "POST api/v1/external-auth/{provider}/link/complete", "绑定本人的外部账号"),
        new(ExemptionKind.SelfService, "DELETE api/v1/external-auth/links/{id:guid}", "解绑本人的外部账号"),
    ];

    /// <summary>Api 程序集里的控制器全部满足约定。</summary>
    [Fact]
    public void Api_controllers_declare_authorization_and_guard_writes_with_policies()
    {
        var controllers = typeof(BaseController).Assembly.GetTypes().Where(IsController).ToArray();

        // 防止反射条件失效后变成"零个控制器、零个问题"的空转通过
        Assert.Contains(typeof(UserController), controllers);
        Assert.Empty(FindViolations(controllers, Exemptions));
    }

    /// <summary>带策略的写端点、只读端点与豁免内的自助端点都合规。</summary>
    [Fact]
    public void Policy_guarded_writes_reads_and_exempted_self_service_endpoints_pass()
    {
        Assert.Empty(FindViolations([typeof(CompliantController)], FixtureExemptions));
    }

    /// <summary>只有类级 [Authorize] 的写端点被拒：这正是"登录即可写"的回退。</summary>
    [Fact]
    public void A_write_endpoint_with_only_class_level_authorize_is_reported()
    {
        var violation = Assert.Single(FindViolations([typeof(ClassLevelOnlyController)], FixtureExemptions));

        Assert.Contains("POST fixtures/class-level", violation);
        Assert.Contains("policy", violation);
    }

    /// <summary>既没有 [Authorize] 也没有 [AllowAnonymous] 的 action 被拒。</summary>
    [Fact]
    public void An_action_without_explicit_authorization_is_reported()
    {
        var violation = Assert.Single(FindViolations([typeof(UndeclaredController)], FixtureExemptions));

        Assert.Contains("GET fixtures/undeclared", violation);
        Assert.Contains("no explicit", violation);
    }

    /// <summary>匿名写端点必须列在匿名协议豁免里；列在自助豁免下不算数。</summary>
    [Fact]
    public void An_anonymous_write_needs_an_anonymous_protocol_exemption()
    {
        var violations = FindViolations([typeof(AnonymousWriteController)], FixtureExemptions);

        var violation = Assert.Single(violations);
        Assert.Contains("POST fixtures/me/anonymous", violation);
        Assert.Contains("anonymous", violation);
        Assert.Empty(FindViolations([typeof(AnonymousWriteController)],
            [new(ExemptionKind.AnonymousProtocol, "POST fixtures/me/anonymous", "夹具：协议端点")]));
    }

    /// <summary>只有 [Route]、没有 HTTP 方法特性的公开方法仍是 action，接受任意方法，被拒；[NonAction] 与 ControllerBase 自带的辅助方法不算。</summary>
    [Fact]
    public void A_route_only_action_without_http_method_is_reported()
    {
        var violation = Assert.Single(FindViolations([typeof(RouteOnlyController)], FixtureExemptions));

        Assert.Contains("fixtures/route-only (RouteOnlyController.Delete)", violation);
        Assert.Contains("no HTTP method attribute", violation);
    }

    private static readonly AuthorizationExemption[] FixtureExemptions =
    [
        new(ExemptionKind.SelfService, "* fixtures/me/**", "夹具：本人自助端点"),
    ];

    private static bool IsController(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(ControllerBase).IsAssignableFrom(type);

    private static List<string> FindViolations(IEnumerable<Type> controllers, IReadOnlyList<AuthorizationExemption> exemptions)
    {
        var violations = new List<string>();
        foreach (var controller in controllers)
        {
            var prefix = controller.GetCustomAttributes<RouteAttribute>(inherit: true).SingleOrDefault()?.Template;
            var classAuthorize = controller.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToArray();
            var classAnonymous = controller.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

            foreach (var action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(IsAction))
            {
                var routes = action.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().ToArray();
                if (routes.Length == 0)
                {
                    // 没有 HTTP 方法特性的 action 仍是端点（[Route] 或类级路由），且接受任意方法，无法按读写判定授权
                    violations.Add($"{CombineRoute(prefix, action.GetCustomAttributes<RouteAttribute>(inherit: true).FirstOrDefault()?.Template)} ({controller.Name}.{action.Name}): action has no HTTP method attribute and accepts every method");
                    continue;
                }

                var authorize = classAuthorize.Concat(action.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>()).ToArray();
                var anonymous = classAnonymous || action.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();
                var hasPolicy = authorize.Any(data => !string.IsNullOrEmpty(data.Policy));

                foreach (var route in routes)
                {
                    var path = CombineRoute(prefix, route.Template);
                    foreach (var method in route.HttpMethods)
                    {
                        var endpoint = $"{method} {path}";
                        if (!anonymous && authorize.Length == 0)
                        {
                            violations.Add($"{endpoint} ({controller.Name}.{action.Name}): no explicit [Authorize] or [AllowAnonymous]");
                            continue;
                        }

                        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
                        {
                            continue;
                        }

                        if (anonymous && !IsExempted(exemptions, ExemptionKind.AnonymousProtocol, method, path))
                        {
                            violations.Add($"{endpoint} ({controller.Name}.{action.Name}): anonymous write endpoint is not an exempted protocol endpoint");
                        }
                        else if (!anonymous && !hasPolicy && !IsExempted(exemptions, ExemptionKind.SelfService, method, path))
                        {
                            violations.Add($"{endpoint} ({controller.Name}.{action.Name}): write endpoint needs an authorization policy or a self-service exemption");
                        }
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// 与 MVC 发现 action 的规则一致：公开、非静态、非抽象、非泛型、非属性访问器、未标 [NonAction]，
    /// 且不是 <see cref="object"/> 或 <see cref="IDisposable"/> 上的方法。
    /// </summary>
    private static bool IsAction(MethodInfo method) =>
        method is { IsPublic: true, IsStatic: false, IsAbstract: false, IsSpecialName: false, IsGenericMethod: false }
        && method.GetBaseDefinition().DeclaringType != typeof(object)
        && !(method.Name == nameof(IDisposable.Dispose) && method.GetParameters().Length == 0 && typeof(IDisposable).IsAssignableFrom(method.DeclaringType))
        && !method.IsDefined(typeof(NonActionAttribute), inherit: true);

    private static string CombineRoute(string? prefix, string? template) => template switch
    {
        null or "" => prefix ?? "",
        _ when template.StartsWith("~/", StringComparison.Ordinal) => template[2..],
        _ => string.IsNullOrEmpty(prefix) ? template : $"{prefix}/{template}",
    };

    private static bool IsExempted(IReadOnlyList<AuthorizationExemption> exemptions, ExemptionKind kind, string method, string path) =>
        exemptions.Any(exemption => exemption.Kind == kind && exemption.Matches(method, path));

    private enum ExemptionKind
    {
        /// <summary>匿名可调用的协议端点：登录、注册、OIDC 协议等</summary>
        AnonymousProtocol,

        /// <summary>已认证用户只作用于本人的端点</summary>
        SelfService,
    }

    /// <summary>豁免项：<c>{HTTP 方法或 *} {路由}</c>，路由以 <c>/**</c> 结尾时匹配该路由本身及其下全部路由。</summary>
    private sealed record AuthorizationExemption(ExemptionKind Kind, string Endpoint, string Reason)
    {
        public bool Matches(string method, string path)
        {
            var separator = Endpoint.IndexOf(' ', StringComparison.Ordinal);
            var exemptMethod = Endpoint[..separator];
            var exemptPath = Endpoint[(separator + 1)..];
            if (exemptMethod != "*" && !string.Equals(exemptMethod, method, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!exemptPath.EndsWith("/**", StringComparison.Ordinal))
            {
                return path == exemptPath;
            }

            var root = exemptPath[..^3];
            return path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
        }
    }

    [Authorize]
    [Route("fixtures")]
    public sealed class CompliantController : ControllerBase
    {
        [HttpGet("read")]
        public void Read() { }

        [Authorize(Policy = "Fixtures.Update")]
        [HttpPut("write")]
        public void Write() { }

        [HttpPost("me/profile")]
        public void UpdateOwnProfile() { }

        [AllowAnonymous]
        [HttpGet("public")]
        public void Public() { }
    }

    [Authorize]
    [Route("fixtures")]
    public sealed class ClassLevelOnlyController : ControllerBase
    {
        [HttpPost("class-level")]
        public void Write() { }
    }

    [Route("fixtures")]
    public sealed class UndeclaredController : ControllerBase
    {
        [HttpGet("undeclared")]
        public void Read() { }
    }

    [Authorize]
    [Route("fixtures")]
    public sealed class RouteOnlyController : ControllerBase
    {
        [Route("route-only")]
        public void Delete() { }

        [NonAction]
        public void Helper() { }
    }

    [Route("fixtures")]
    public sealed class AnonymousWriteController : ControllerBase
    {
        [AllowAnonymous]
        [HttpPost("me/anonymous")]
        public void Write() { }
    }
}
