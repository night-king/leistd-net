namespace CompanyName.ProjectName.Client.Dtos;

/// <summary>批量操作的逐条结果；每条单独提交，部分失败不回滚已成功的条目。</summary>
/// <param name="Items">逐条结果，重复的 Id 只出现一次</param>
public sealed record BatchOperationDto(IReadOnlyList<BatchOperationItemDto> Items);

/// <summary>批量操作中一条的结果。</summary>
/// <param name="Id">用户 Id</param>
/// <param name="Succeeded">是否成功</param>
/// <param name="ErrorCode">失败时的业务错误码，与单个操作被拒时的 <c>code</c> 相同</param>
/// <param name="Message">失败时按请求语言给出的说明</param>
public sealed record BatchOperationItemDto(Guid Id, bool Succeeded, string? ErrorCode, string? Message);
