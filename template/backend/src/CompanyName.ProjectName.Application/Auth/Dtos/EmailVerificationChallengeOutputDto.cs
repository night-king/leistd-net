namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>邮箱验证挑战的公开信息。</summary>
public sealed record EmailVerificationChallengeOutputDto
{
    public required Guid ChallengeId { get; init; }

    public required int ExpiresInSeconds { get; init; }

    public required int RetryAfterSeconds { get; init; }
}
