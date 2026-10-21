using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Leistd.Security.AspNetCore.Cookies;

// 在全部 PostConfigure 之后检查接入方案仍挂着组件包装；后置整体替换会丢掉引用版本契约。
internal sealed class TicketCookieOptionsValidator(IReadOnlySet<string> schemes) : IValidateOptions<CookieAuthenticationOptions>
{
    private const string Order = "configure it before the AddDistributedTicketStore post-configuration runs (in AddCookie or Configure); the component wraps it.";

    public ValidateOptionsResult Validate(string? name, CookieAuthenticationOptions options)
    {
        if (name is null || !schemes.Contains(name)) return ValidateOptionsResult.Skip;
        var failures = new List<string>();
        if (options.Events is not CookieTicketEvents)
            failures.Add($"CookieAuthenticationOptions.Events for cookie scheme '{name}' was replaced after the distributed ticket store wrapped it; {Order}");
        if (options.EventsType is not null)
            failures.Add($"CookieAuthenticationOptions.EventsType for cookie scheme '{name}' was set after the distributed ticket store wrapped the events; {Order}");
        if (options.CookieManager is not DistributedTicketStore.ReferenceCookieManager)
            failures.Add($"CookieAuthenticationOptions.CookieManager for cookie scheme '{name}' was replaced after the distributed ticket store wrapped it; {Order}");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
