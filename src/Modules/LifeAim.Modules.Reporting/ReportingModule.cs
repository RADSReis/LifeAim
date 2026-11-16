using LifeAim.BuildingBlocks;
using LifeAim.Modules.Reporting.Application;
using LifeAim.Modules.Reporting.Contracts;
using LifeAim.Modules.Reporting.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeAim.Modules.Reporting;

public static class ReportingModule
{
    /// <summary>The "reporting" schema and its embedded SQL scripts, for LifeAim.Migrator.</summary>
    public static ModuleMigrations Migrations { get; } = new("reporting", typeof(ReportingModule).Assembly);

    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SummaryRepository>();
        services.AddSingleton<IReportingService, ReportingService>();
        return services;
    }
}
