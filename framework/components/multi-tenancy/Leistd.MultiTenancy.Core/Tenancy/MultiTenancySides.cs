namespace Leistd.MultiTenancy.Tenancy;

/// <summary>指定能力适用的多租户侧别。</summary>
[Flags]
public enum MultiTenancySides
{
    /// <summary>租户侧。</summary>
    Tenant = 1,

    /// <summary>宿主侧。</summary>
    Host = 2,

    /// <summary>租户侧和宿主侧。</summary>
    Both = Tenant | Host
}
