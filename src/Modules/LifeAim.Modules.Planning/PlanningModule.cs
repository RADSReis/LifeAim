using LifeAim.BuildingBlocks;
using LifeAim.Modules.Planning.Application;
using LifeAim.Modules.Planning.Contracts;
using LifeAim.Modules.Planning.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeAim.Modules.Planning;

public static class PlanningModule
{
    /// <summary>The "planning" schema and its embedded SQL scripts, for LifeAim.Migrator.</summary>
    public static ModuleMigrations Migrations { get; } = new("planning", typeof(PlanningModule).Assembly);

    public static IServiceCollection AddPlanningModule(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ProposalRepository>();
        services.AddSingleton<IPlanningService, PlanningService>();
        return services;
    }
}
