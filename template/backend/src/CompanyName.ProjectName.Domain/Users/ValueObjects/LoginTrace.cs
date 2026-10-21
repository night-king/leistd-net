#if (LocalIdentity)
namespace CompanyName.ProjectName.Domain.Users.ValueObjects;

/// <summary>最近一次成功登录的时刻与来源 IP。</summary>
public sealed record LoginTrace
{
    public DateTime Time { get; private init; }

    public string? Ip { get; private init; }

    private LoginTrace()
    {
    }

    public LoginTrace(DateTime time, string? ip)
    {
        Time = time;
        Ip = ip;
    }
}
#endif
