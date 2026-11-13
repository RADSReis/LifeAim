using LifeAim.BuildingBlocks;
using LifeAim.Modules.Goals.Application;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Goals.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeAim.Modules.Goals;

public static class GoalsModule
{
    /// <summary>The "goals" schema and the SQL scripts embedded in this assembly, for LifeAim.Migrator.</summary>
    public static ModuleMigrations Migrations { get; } = new("goals", typeof(GoalsModule).Assembly);

    public static IServiceCollection AddGoalsModule(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<GoalRepository>();
        services.AddSingleton<IGoalService, GoalService>();
        return services;
    }
}
