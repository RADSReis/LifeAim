using LifeAim.BuildingBlocks;
using LifeAim.Modules.Communication.Infrastructure;
using LifeAim.Modules.Communication.Infrastructure.DiscordBot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeAim.Modules.Communication;

public static class CommunicationModule
{
    /// <summary>The "communication" schema and its embedded SQL scripts, for LifeAim.Migrator.</summary>
    public static ModuleMigrations Migrations { get; } = new("communication", typeof(CommunicationModule).Assembly);

    public static IServiceCollection AddCommunicationModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DiscordOptions>(configuration.GetSection(DiscordOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ChannelUserRepository>();

        // A hosted service starts with the API and stops with it.
        services.AddHostedService<DiscordBotService>();

        return services;
    }
}
