using Leistd.Localization.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.Localization.AspNetCore.HostedServices;

// 按 SupportedUICultures 预热资源缓存，不另设语言清单；读取器按自身规则处理坏资源。
internal sealed class LocalizationPreloadHostedService(
    JsonLocalizationResourceReader reader,
    IOptions<RequestLocalizationOptions> requestLocalizationOptions,
    ILogger<LocalizationPreloadHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var cultures = requestLocalizationOptions.Value.SupportedUICultures;
        if (cultures is null)
            return Task.CompletedTask;

        foreach (var culture in cultures)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var count = reader.GetTexts(culture.Name).Count;
            logger.LogInformation("Localization warm-up: culture '{Culture}' loaded {Count} text entries.", culture.Name, count);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
