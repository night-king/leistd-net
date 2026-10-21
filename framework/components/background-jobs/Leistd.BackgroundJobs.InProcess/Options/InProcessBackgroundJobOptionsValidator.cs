using Microsoft.Extensions.Options;

namespace Leistd.BackgroundJobs.InProcess.Options;

// 容量越界在启动期拒绝，不在建队列时静默改写
internal sealed class InProcessBackgroundJobOptionsValidator(string configSectionPath) : IValidateOptions<InProcessBackgroundJobOptions>
{
    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath { get; } = configSectionPath;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, InProcessBackgroundJobOptions options)
        => options.QueueCapacity >= 1
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ConfigSectionPath}:QueueCapacity must be at least 1 " +
                $"(was {options.QueueCapacity}).");
}
