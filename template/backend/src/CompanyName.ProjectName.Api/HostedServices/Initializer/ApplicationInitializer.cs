using CompanyName.ProjectName.Application.Initialization;

namespace CompanyName.ProjectName.Api.HostedServices.Initializer;

/// <summary>
/// 应用程序启动引导程序
/// 负责在应用开始接收请求前执行必要的初始化任务（如数据初始化、缓存预热等）
/// </summary>
public class ApplicationInitializer(
    IServiceProvider serviceProvider,
    ILogger<ApplicationInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting application bootstrap");
        using var scope = serviceProvider.CreateScope();

        // 1. 初始化系统数据（角色、用户、权限）
        // 这是系统首次启动必须完成的基础数据初始化
        var systemInitializer = scope.ServiceProvider.GetRequiredService<ISystemInitializer>();
        await systemInitializer.InitializeAsync(cancellationToken);

        logger.LogInformation("Application bootstrap completed");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
