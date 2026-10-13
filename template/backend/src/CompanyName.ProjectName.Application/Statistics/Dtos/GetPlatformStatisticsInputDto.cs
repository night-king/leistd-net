using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Statistics.Dtos;

/// <summary>获取平台统计输入 DTO：一段日历区间，以及解释这段日期的时区。</summary>
/// <remarks>
/// 区间是日历日期而不是时刻：服务端按 <see cref="TimeZone"/> 把它换算成 UTC，并用同一个时区切分统计桶。
/// 时区由调用方显式给出，取它实际用来展示时间的那个——展示设置为空时浏览器用自己的时区，服务端无从得知。
/// </remarks>
public record GetPlatformStatisticsInputDto : IValidatableObject
{
    /// <summary>区间最多包含的日数。</summary>
    public const int MaxDays = 366;

    /// <summary>起始日（含）。</summary>
    [Display(Name = "Start date")]
    [Required(ErrorMessage = "{0} is required.")]
    public DateOnly? StartDate { get; init; }

    /// <summary>结束日（含），不得晚于 <see cref="TimeZone"/> 的今天。</summary>
    [Display(Name = "End date")]
    [Required(ErrorMessage = "{0} is required.")]
    public DateOnly? EndDate { get; init; }

    /// <summary>解释日期的时区，IANA 名（如 <c>Asia/Shanghai</c>）。</summary>
    [Display(Name = "Time zone")]
    [Required(ErrorMessage = "{0} is required.")]
    [MaxLength(64, ErrorMessage = "{0} cannot exceed {1} characters.")]
    public string? TimeZone { get; init; }

    /// <inheritdoc />
    /// <remarks>
    /// 只校验不依赖服务的结构规则；时区是否可用、结束日是否晚于今天要用时区与时钟判断，由应用服务校验，
    /// 同样作为字段错误返回。文案是本地化键。
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate is not { } start || EndDate is not { } end)
        {
            yield break;
        }

        var days = end.DayNumber - start.DayNumber + 1;
        if (end < start)
        {
            yield return new ValidationResult("The end date cannot be earlier than the start date.", [nameof(EndDate)]);
        }
        else if (days > MaxDays)
        {
            yield return new ValidationResult("The date range cannot exceed 366 days.", [nameof(EndDate)]);
        }
        else if (start.DayNumber - days < DateOnly.MinValue.DayNumber + 1)
        {
            // 上一区间从起始日前移同样的天数，本地零点换算 UTC 最多再早 14 小时：都要落在 DateTime 能表示的范围内
            yield return new ValidationResult("The start date is too early.", [nameof(StartDate)]);
        }
    }
}
