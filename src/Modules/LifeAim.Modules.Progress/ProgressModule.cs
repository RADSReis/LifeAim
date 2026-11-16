using LifeAim.BuildingBlocks;
using LifeAim.Modules.Progress.Application;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Progress.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeAim.Modules.Progress;

public static class ProgressModule
{
    /// <summary>The "progress" schema and its embedded SQL scripts, for LifeAim.Migrator.</summary>
    public static ModuleMigrations Migrations { get; } = new("progress", typeof(ProgressModule).Assembly);

    public static IServiceCollection AddProgressModule(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ProgressRepository>();
        services.AddSingleton<IProgressService, ProgressService>();
        return services;
    }
}
