using LifeAim.Modules.Agents.Application;
using LifeAim.Modules.Agents.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace LifeAim.Modules.Agents;

public static class AgentsModule
{
    public static IServiceCollection AddAgentsModule(this IServiceCollection services)
    {
        services.AddSingleton<IPippin, PlaceholderPippin>();
        return services;
    }
}
