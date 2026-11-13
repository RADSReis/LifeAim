using LifeAim.BuildingBlocks;
using Microsoft.Extensions.DependencyInjection;

namespace LifeAim.Modules.Goals;

public static class GoalsModule
{
    /// <summary>The "goals" schema and the SQL scripts embedded in this assembly, for LifeAim.Migrator.</summary>
    public static ModuleMigrations Migrations { get; } = new("goals", typeof(GoalsModule).Assembly);

    public static IServiceCollection AddGoalsModule(this IServiceCollection services)
    {
        return services;
    }
}
