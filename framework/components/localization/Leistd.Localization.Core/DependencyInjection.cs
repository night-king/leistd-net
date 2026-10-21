using System.Reflection;
using Leistd.Localization.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.Localization;

/// <summary>组件登记自带默认译文的入口。</summary>
public static class DependencyInjection
{
    /// <summary>把程序集里嵌入的 <c>Resources/{culture}.json</c> 登记为 JSON 本地化资源，供该组件发出的错误码取默认文案。</summary>
    /// <remarks>
    /// <para>发出错误码的组件在自己的 <c>Add*</c> 里调用一次，宿主不必知道组件有哪些词条。
    /// 未注册 JSON 本地化时登记无副作用：错误响应退回异常自带的英文诊断。</para>
    /// <para>登记位置恒在最前：同一键后登记者生效，因此宿主资源总能覆盖组件默认文案，与调用顺序无关。
    /// 重复登记同一程序集只保留一份。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public static IServiceCollection AddOrders(this IServiceCollection services)
    /// {
    ///     services.AddJsonLocalizationResources(typeof(OrderService).Assembly);
    ///     return services;
    /// }
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="assembly">嵌入了 <c>Resources/{culture}.json</c> 的程序集。</param>
    public static IServiceCollection AddJsonLocalizationResources(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        services.Configure<JsonLocalizationOptions>(options =>
        {
            if (!options.ResourceAssemblies.Contains(assembly))
            {
                options.ResourceAssemblies.Insert(0, assembly);
            }
        });
        return services;
    }
}
