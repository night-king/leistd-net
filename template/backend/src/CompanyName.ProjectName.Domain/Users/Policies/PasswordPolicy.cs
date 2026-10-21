#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.ExceptionHandling;

namespace CompanyName.ProjectName.Domain.Users.Policies;

/// <summary>服务端权威密码策略。</summary>
/// <remarks>
/// <para><b>唯一权威。</b>DTO 的 DataAnnotations 与前端表单只负责快速反馈，不承担安全不变量——
/// 直接调 API 能绕开它们，内部调用（种子、租户初始化、bootstrap）连 DTO 都不经过。
/// <b>不要在各入口另写一套规则</b>：多套规则并存时，最宽的那一套就是系统的真实下限，
/// 而"哪一套最宽"没人会持续核对。新增创建口令的入口一律调用本类。</para>
/// <para><b>选长度而不选复杂度正则。</b>复杂度规则把用户推向 <c>Passw0rd!</c> 这类可预测形态，
/// 却挡不住它；而长度直接增加搜索空间。因此这里只有一条规则：足够长，且允许长口令。</para>
/// <para><b>不内置弱口令名单。</b>通用模板不该替业务项目钉死"哪些口令算弱"——名单短了没有实效，
/// 长了就是把一份会过时的数据塞进模板。要拦已泄漏口令，在业务项目里接一份泄漏库
/// （如 Have I Been Pwned 的 k-anonymity 接口）并在本类之外的调用链上加一步校验；
/// 本类保持"长度即策略"，各入口仍只调它一处。</para>
/// <para>上限存在只为防御拒绝服务（哈希开销随输入增长），不是安全要求，所以设得很宽。</para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>最小长度</summary>
    public const int MinimumLength = 12;

    /// <summary>最大长度。仅为防御哈希开销型拒绝服务，不是安全要求</summary>
    public const int MaximumLength = 256;

    /// <summary>密码是否满足策略</summary>
    public static bool IsAcceptable(string? password) => Describe(password) is null;

    /// <summary>校验密码，不通过则抛 <see cref="BusinessException"/>。</summary>
    /// <remarks>
    /// 每种不通过的原因带自己的错误码与占位参数，界面上看到的才是"密码长度至少 12 个字符"
    /// 这样的具体原因。不带码时没有词条可查，中文界面上只会出现构造时那句英文诊断串。
    /// <para>
    /// <paramref name="subject"/> 只进异常消息（供日志与无本地化形态），不进本地化文案：
    /// 它是 <c>DefaultAdmin:Password</c> 这类内部标识，不该出现在给用户看的句子里。
    /// </para>
    /// </remarks>
    /// <param name="password">待校验口令</param>
    /// <param name="subject">用于错误消息与日志的主体描述，如 <c>"DefaultAdmin:Password"</c></param>
    public static void Ensure(string? password, string subject)
    {
        var problem = Describe(password);
        if (problem is null)
        {
            return;
        }

        var exception = new BusinessException(problem.Value.Code, $"{subject} {problem.Value.Message}");
        foreach (var (name, value) in problem.Value.Data)
        {
            exception.WithData(name, value);
        }
        throw exception;
    }

    /// <summary>不满足策略的原因：错误码、英文消息与本地化占位参数。</summary>
    private readonly record struct Problem(
        string Code,
        string Message,
        (string Name, object? Value)[] Data);

    /// <summary>返回不满足策略的原因；满足则返回 <see langword="null"/></summary>
    private static Problem? Describe(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return new Problem(SecurityErrorCodes.PasswordRequired, "is required.", []);
        }

        if (password.Length < MinimumLength)
        {
            return new Problem(
                SecurityErrorCodes.PasswordTooShort,
                $"must be at least {MinimumLength} characters long.",
                [("MinimumLength", MinimumLength)]);
        }

        if (password.Length > MaximumLength)
        {
            return new Problem(
                SecurityErrorCodes.PasswordTooLong,
                $"must be at most {MaximumLength} characters long.",
                [("MaximumLength", MaximumLength)]);
        }

        return null;
    }
}
#endif
