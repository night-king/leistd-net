namespace CompanyName.ProjectName.IntegrationTests;

public sealed class RunnerContainerOwnershipTests
{
    private const string Labels = """
        {"remote-project-runner.managed":"true","remote-project-runner.task":"task-1","remote-project-runner.attempt":"attempt-1"}
        """;

    [Fact]
    public void No_runner_environment_preserves_default_behavior()
    {
        Assert.Null(RunnerContainerOwnership.Parse(null, null));
    }

    [Fact]
    public void Ownership_preserves_all_supplied_labels()
    {
        var ownership = Assert.IsType<RunnerContainerOwnership>(RunnerContainerOwnership.Parse(Labels, "rpr-example"));
        Assert.Equal("rpr-example", ownership.NamePrefix);
        Assert.Equal("attempt-1", ownership.Labels["remote-project-runner.attempt"]);
        Assert.Equal(3, ownership.Labels.Count);
    }

    [Theory]
    [InlineData(null, "rpr-example")]
    [InlineData(Labels, null)]
    [InlineData(Labels, "")]
    [InlineData(Labels, "../invalid")]
    [InlineData("not-json", "rpr-example")]
    [InlineData("{}", "rpr-example")]
    [InlineData("null", "rpr-example")]
    [InlineData("{\"private-input\":\"do-not-log\"}", "rpr-example")]
    [InlineData("{\"remote-project-runner.managed\":false}", "rpr-example")]
    public void Incomplete_or_invalid_ownership_fails_without_echoing_input(string? labels, string? prefix)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RunnerContainerOwnership.Parse(labels, prefix));
        Assert.DoesNotContain("do-not-log", error.ToString());
        Assert.DoesNotContain("not-json", error.ToString());
    }
}
