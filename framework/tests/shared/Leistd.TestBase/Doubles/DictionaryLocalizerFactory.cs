using Microsoft.Extensions.Localization;

namespace Leistd.TestBase.Doubles;

/// <summary>以固定字典作为译文来源的本地化工厂，不区分资源类型与文化。</summary>
/// <remarks>字典里没有的键按 <c>ResourceNotFound</c> 返回键本身，与官方实现缺译文时的形态一致。</remarks>
/// <param name="texts">键到译文的映射。</param>
public sealed class DictionaryLocalizerFactory(Dictionary<string, string> texts) : IStringLocalizerFactory
{
    /// <inheritdoc/>
    public IStringLocalizer Create(Type resourceSource) => new DictionaryLocalizer(texts);

    /// <inheritdoc/>
    public IStringLocalizer Create(string baseName, string location) => new DictionaryLocalizer(texts);

    private sealed class DictionaryLocalizer(Dictionary<string, string> texts) : IStringLocalizer
    {
        public LocalizedString this[string name]
            => texts.TryGetValue(name, out var value) ? new(name, value) : new(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
