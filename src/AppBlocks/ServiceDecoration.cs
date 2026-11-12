using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppBlocks;

/// <summary>Preserves an existing dependency-injection registration when a companion decorates it.</summary>
public static class ServiceDecoration
{
    public static object Create(IServiceProvider provider, ServiceDescriptor descriptor) => descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

    public static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> factory) where T : class
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(T));
        services.Replace(new ServiceDescriptor(typeof(T), sp => factory(sp, (T)Create(sp, descriptor)), descriptor.Lifetime));
    }
}
