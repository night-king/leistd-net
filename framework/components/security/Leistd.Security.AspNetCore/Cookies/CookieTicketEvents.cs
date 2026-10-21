using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.Security.AspNetCore.Cookies;

// 默认委托转发原事件对象的虚方法，宿主后置改 OnXxx 按原生语义取代对应转发；只有 SigningIn 追加版本职责。
internal sealed class CookieTicketEvents : CookieAuthenticationEvents
{
    private readonly CookieAuthenticationEvents _original;
    private readonly Type? _originalType;

    public CookieTicketEvents(CookieAuthenticationEvents original, Type? originalType)
    {
        _original = original;
        _originalType = originalType;
        OnValidatePrincipal = context => GetOriginal(context.HttpContext).ValidatePrincipal(context);
        OnCheckSlidingExpiration = context => GetOriginal(context.HttpContext).CheckSlidingExpiration(context);
        OnSigningIn = context => GetOriginal(context.HttpContext).SigningIn(context);
        OnSignedIn = context => GetOriginal(context.HttpContext).SignedIn(context);
        OnSigningOut = context => GetOriginal(context.HttpContext).SigningOut(context);
        OnRedirectToLogin = context => GetOriginal(context.HttpContext).RedirectToLogin(context);
        OnRedirectToAccessDenied = context => GetOriginal(context.HttpContext).RedirectToAccessDenied(context);
        OnRedirectToLogout = context => GetOriginal(context.HttpContext).RedirectToLogout(context);
        OnRedirectToReturnUrl = context => GetOriginal(context.HttpContext).RedirectToReturnUrl(context);
    }

    // EventsType 按请求解析一次，同请求同方案共用实例。
    private CookieAuthenticationEvents GetOriginal(HttpContext context)
    {
        if (_originalType is null) return _original;
        if (context.Items.TryGetValue(this, out var cached)) return (CookieAuthenticationEvents)cached!;
        var events = (CookieAuthenticationEvents)context.RequestServices.GetRequiredService(_originalType);
        context.Items[this] = events;
        return events;
    }

    public override async Task SigningIn(CookieSigningInContext context)
    {
        await base.SigningIn(context);
        context.Properties.Items[DistributedTicketStore.ReferenceVersion] = DistributedTicketStore.NewVersion();
        context.HttpContext.Items[DistributedTicketStore.ExplicitSignInKey(context.Scheme.Name)] = true;
    }
}
