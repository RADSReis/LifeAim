using Microsoft.Extensions.DependencyInjection;

namespace LifeAim.Modules.Progress;

public static class ProgressModule
{
    public static IServiceCollection AddProgressModule(this IServiceCollection services)
    {
        return services;
    }
}
