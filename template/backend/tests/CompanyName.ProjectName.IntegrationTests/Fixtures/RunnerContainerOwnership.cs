using System.Text.Json;
using System.Text.RegularExpressions;
using Testcontainers.PostgreSql;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>可选执行器归属协议；未提供协议时保留 Testcontainers 默认行为。</summary>
internal sealed record RunnerContainerOwnership(string NamePrefix, IReadOnlyDictionary<string, string> Labels)
{
    public static RunnerContainerOwnership? Parse(string? labelsJson, string? namePrefix)
    {
        if (labelsJson is null && namePrefix is null)
        {
            return null;
        }

        const string error = "Invalid runner container ownership: supply both RPR_DOCKER_LABELS and RPR_DOCKER_NAME_PREFIX.";
        if (string.IsNullOrWhiteSpace(labelsJson) || namePrefix is null ||
            !Regex.IsMatch(namePrefix, "^[a-z0-9][a-z0-9_.-]{0,150}$"))
        {
            throw new InvalidOperationException(error);
        }

        Dictionary<string, string>? labels;
        try
        {
            labels = JsonSerializer.Deserialize<Dictionary<string, string>>(labelsJson);
        }
        catch (JsonException)
        {
            // 不把原始输入或解析异常附入诊断，标签可能来自调用方环境。
            throw new InvalidOperationException(error);
        }

        if (labels is null || labels.Count == 0 ||
            labels.Any(pair => !pair.Key.StartsWith("remote-project-runner.", StringComparison.Ordinal) ||
                               string.IsNullOrWhiteSpace(pair.Value)) ||
            !labels.TryGetValue("remote-project-runner.managed", out var managed) || managed != "true" ||
            !labels.ContainsKey("remote-project-runner.task") || !labels.ContainsKey("remote-project-runner.attempt"))
        {
            throw new InvalidOperationException(error);
        }

        return new RunnerContainerOwnership(namePrefix, labels);
    }

    public PostgreSqlBuilder Apply(PostgreSqlBuilder builder)
    {
        builder = builder.WithName($"{NamePrefix}-postgres-{Guid.NewGuid():N}");
        foreach (var (key, value) in Labels)
        {
            builder = builder.WithLabel(key, value);
        }

        return builder;
    }
}
