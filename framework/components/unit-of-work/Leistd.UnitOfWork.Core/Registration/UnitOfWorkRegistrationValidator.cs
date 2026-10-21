using System.Reflection;
using Leistd.UnitOfWork.Attributes;
using Leistd.EventBus.EventHandlers;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.UnitOfWork.Registration;

// 在容器构建时拒绝从服务描述符可确定的代理失效。
// 不执行工厂来猜测隐藏实现类型，以免提前解析依赖并触发副作用。
internal static class UnitOfWorkRegistrationValidator
{
    /// <summary>校验工作单元相关的注册形式；不通过抛 <see cref="InvalidOperationException"/>。</summary>
    /// <remarks>
    /// 必须在任何注册回调改写描述符之前执行，否则读到的都是织入后的工厂型描述符。
    /// 调用时机由 <c>AddRegistrationValidator</c> 保证。
    /// </remarks>
    public static void Validate(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            ValidateEventHandler(descriptor);
            ValidateOpenGenericUnitOfWork(descriptor);
        }
    }

    // Microsoft DI 不支持开放泛型服务类型与代理工厂组合。
    private static void ValidateOpenGenericUnitOfWork(ServiceDescriptor descriptor)
    {
        if (!descriptor.ServiceType.IsGenericTypeDefinition ||
            descriptor.ImplementationType is not { } implementationType ||
            !DeclaresUnitOfWork(implementationType))
        {
            return;
        }

        throw new InvalidOperationException(
            $"[UnitOfWork] on the open generic implementation '{implementationType.FullName}' has no " +
            $"effect: '{descriptor.ServiceType.FullName}' is an open generic service type, which cannot " +
            "be woven with an interceptor. Register closed generic service types, or move the " +
            "transaction boundary to a non-generic service.");
    }

    private static bool DeclaresUnitOfWork(Type type)
    {
        if (type.GetCustomAttributes(typeof(UnitOfWorkAttribute), true).Length > 0)
        {
            return true;
        }

        return type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(method => method.GetCustomAttributes(typeof(UnitOfWorkAttribute), true).Length > 0);
    }

    // 事件处理器必须织入以执行阶段过滤，否则会在两个阶段各执行一次。
    // 工厂注册可由服务类型织入；开放泛型受 Microsoft DI 限制。
    private static void ValidateEventHandler(ServiceDescriptor descriptor)
    {
        var serviceType = descriptor.ServiceType;
        if (!serviceType.IsGenericType ||
            serviceType.GetGenericTypeDefinition() != typeof(IEventHandler<>))
        {
            return;
        }

        if (serviceType.IsGenericTypeDefinition)
        {
            throw new InvalidOperationException(
                "Open generic event handler registrations cannot participate in unit-of-work phase " +
                $"filtering: '{Describe(descriptor)}'. Register closed generic service types " +
                "(one registration per event type) instead; otherwise the handler runs once before " +
                "the commit and once after it.");
        }
    }

    private static string Describe(ServiceDescriptor descriptor)
    {
        var implementation = descriptor.ImplementationType?.FullName
            ?? descriptor.ImplementationInstance?.GetType().FullName
            ?? "<factory>";
        return $"{descriptor.ServiceType.FullName} -> {implementation}";
    }
}
