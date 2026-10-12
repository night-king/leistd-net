using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Statistics.Dtos;

/// <summary>获取平台统计输入 DTO。</summary>
public record GetPlatformStatisticsInputDto
{
    /// <summary>统计区间的天数，只收 7 或 30；省略时为 7。</summary>
    /// <remarks>区间按 UTC 日划分：含今天在内往前共这么多个整日，上一区间是紧邻其前的等长区间。</remarks>
    [Display(Name = "Days")]
    [AllowedValues(7, 30, ErrorMessage = "{0} is not an allowed value.")]
    public int Days { get; init; } = 7;
}
