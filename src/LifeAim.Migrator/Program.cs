// LifeAim.Migrator: applies every module's pending SQL migrations, then exits.
// Exit code 0 = database up to date; 1 = something failed (the deploy must stop before the API).

using DbUp;
using LifeAim.BuildingBlocks;
using LifeAim.Modules.Communication;
using LifeAim.Modules.Goals;
using Microsoft.Extensions.Configuration;
using Npgsql;

// Each module that owns database tables adds its line here.
ModuleMigrations[] modules =
[
    GoalsModule.Migrations,
    CommunicationModule.Migrations,
];

// Locally: User Secrets (shared with the API). On the VPS: ConnectionStrings__LifeAim.
var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var connectionString = configuration.GetConnectionString("LifeAim");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings:LifeAim is not configured.");
    return 1;
}

foreach (var module in modules)
{
    Console.WriteLine($"== {module.Schema}");

    // DbUp keeps its journal inside the module's schema, so the schema must exist first.
    await using (var connection = new NpgsqlConnection(connectionString))
    {
        await connection.OpenAsync();
        var schema = new NpgsqlCommandBuilder().QuoteIdentifier(module.Schema);
        await using var command = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {schema};", connection);
        await command.ExecuteNonQueryAsync();
    }

    var upgrader = DeployChanges.To
        .PostgresqlDatabase(connectionString, module.Schema)
        .WithScriptsEmbeddedInAssembly(
            module.Assembly,
            name => name.Contains(".Infrastructure.Migrations.") && name.EndsWith(".sql"))
        .JournalToPostgresqlTable(module.Schema, "schema_versions")
        .WithTransactionPerScript()
        .LogToConsole()
        .Build();

    var result = upgrader.PerformUpgrade();
    if (!result.Successful)
    {
        Console.Error.WriteLine($"Migrations of '{module.Schema}' failed: {result.Error.Message}");
        return 1;
    }
}

Console.WriteLine("Database is up to date.");
return 0;
