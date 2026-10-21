using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Leistd.Settings.Hosting.Configuration;

// 宿主级设置作为配置源：有值的项覆盖部署配置的对应键，没有值的项回落到配置文件。
// 数据变化时 OnReload，按配置节绑定的 IOptionsMonitor 随之重算。
internal sealed class HostSettingsConfigurationProvider : ConfigurationProvider
{
    // 写入后的立即应用与周期刷新可能同时到来，换数据与重载要成对完成
    // 写全名：引用 Leistd.Lock 包后，Leistd.* 命名空间内的裸 Lock 会解析成该命名空间
    private readonly System.Threading.Lock _gate = new();
    private Dictionary<string, string?>? _rejected;

    public bool IsAttached { get; private set; }

    public void MarkAttached() => IsAttached = true;

    // 换上新的一组配置键值；新值让某个 Options 校验不过时换回上一组再重载，整组不生效。
    // 校验失败来自重载回调（已订阅的 IOptionsMonitor）或 validate（无人订阅的选项）。
    // 与当前一组或上次被拒的一组相同时不做任何事，周期刷新不重复重算或报错。
    public IReadOnlyList<string> Apply(IReadOnlyDictionary<string, string?> values, Func<IReadOnlyList<string>> validate)
    {
        lock (_gate)
        {
            if (Matches(Data, values) || (_rejected is not null && Matches(_rejected, values)))
            {
                return [];
            }

            var previous = Data;
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            var failures = Reload();
            if (failures.Count == 0)
            {
                failures = [.. validate()];
            }

            if (failures.Count == 0)
            {
                _rejected = null;
                return [];
            }

            Data = previous;
            _rejected = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            // 换回的上一组若也不合规，是部署配置本身有误，不是这里能纠正的
            _ = Reload();
            return failures;
        }
    }

    // 重载并收集 Options 的校验失败；其它异常照常抛出
    private IReadOnlyList<string> Reload()
    {
        try
        {
            OnReload();
            return [];
        }
        // 重载回调逐层包 AggregateException，展平后再判断
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(inner => inner is OptionsValidationException))
        {
            return [.. ex.Flatten().InnerExceptions.Select(inner => inner.Message)];
        }
        catch (OptionsValidationException ex)
        {
            return [ex.Message];
        }
    }

    private static bool Matches(IDictionary<string, string?> current, IReadOnlyDictionary<string, string?> values) =>
        current.Count == values.Count &&
        values.All(pair => current.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

// 只产出同一个提供程序实例：应用器往 DI 里那个实例推数据
internal sealed class HostSettingsConfigurationSource(HostSettingsConfigurationProvider provider) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        provider.MarkAttached();
        return provider;
    }
}
