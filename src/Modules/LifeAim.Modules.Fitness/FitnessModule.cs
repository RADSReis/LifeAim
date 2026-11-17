using LifeAim.BuildingBlocks;
using LifeAim.Modules.Fitness.Application;
using LifeAim.Modules.Fitness.Contracts;
using LifeAim.Modules.Fitness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeAim.Modules.Fitness;

public static class FitnessModule
{
    /// <summary>The "fitness" schema and its embedded SQL scripts, for LifeAim.Migrator.</summary>
    public static ModuleMigrations Migrations { get; } = new("fitness", typeof(FitnessModule).Assembly);

    public static IServiceCollection AddFitnessModule(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<FitnessRepository>();
        services.AddSingleton<IFitnessJournal, FitnessJournal>();
        services.AddSingleton<ReminderRepository>();
        services.AddSingleton<IFitnessReminders, FitnessReminders>();
        return services;
    }
}
