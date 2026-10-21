namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>批量操作输出 DTO：逐条结果，按请求中首次出现的顺序排列，重复的 Id 只出现一次。</summary>
/// <remarks>每条单独提交，失败的条目不影响其他条目，已成功的也不会因后面的失败回滚。</remarks>
public record BatchOperationOutputDto
{
    public required IReadOnlyList<BatchOperationItemOutputDto> Items { get; init; }
}

/// <summary>批量操作中一条的结果。</summary>
public record BatchOperationItemOutputDto
{
    public required Guid Id { get; init; }

    /// <summary>是否成功；状态本来就是目标值（如已启用再启用）也算成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>失败时的业务错误码，与单个操作被拒时响应里的 <c>code</c> 相同；成功时为 null。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>失败时按请求语言给出的说明，与单个操作被拒时响应里的 <c>detail</c> 相同；成功时为 null。</summary>
    public string? Message { get; init; }
}
