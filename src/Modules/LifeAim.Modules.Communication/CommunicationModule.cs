using LifeAim.Modules.Communication.Infrastructure.DiscordBot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LifeAim.Modules.Communication;

public static class CommunicationModule
{
    public static IServiceCollection AddCommunicationModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DiscordOptions>(configuration.GetSection(DiscordOptions.SectionName));

        // A hosted service starts with the API and stops with it.
        services.AddHostedService<DiscordBotService>();

        return services;
    }
}
