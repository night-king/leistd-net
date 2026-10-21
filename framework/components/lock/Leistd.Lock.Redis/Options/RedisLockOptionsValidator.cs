using Microsoft.Extensions.Options;

namespace Leistd.Lock.Redis.Options;

// 启动期校验租约与重试间隔为正；键前缀不作格式限制。报错键名按实际绑定的配置节给出。
internal sealed class RedisLockOptionsValidator(string sectionPath = RedisLockOptions.SectionName) : IValidateOptions<RedisLockOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RedisLockOptions options)
    {
        var failures = new List<string>();

        if (options.Expiry <= TimeSpan.Zero)
        {
            failures.Add(
                $"{sectionPath}:Expiry must be greater than zero (was {options.Expiry}). It is the lock lease; " +
                "a non-positive value makes the lock expire immediately or be rejected by Redis, " +
                "so mutual exclusion silently stops holding.");
        }

        if (options.RetryInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{sectionPath}:RetryInterval must be greater than zero (was {options.RetryInterval}). " +
                "A non-positive value turns lock acquisition into a busy loop against Redis.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
