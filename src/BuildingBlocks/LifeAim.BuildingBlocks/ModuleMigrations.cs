using System.Reflection;

namespace LifeAim.BuildingBlocks;

/// <summary>
/// What a module tells LifeAim.Migrator: the PostgreSQL schema it owns and the assembly whose
/// embedded Infrastructure/Migrations/*.sql scripts create and evolve that schema.
/// </summary>
public sealed record ModuleMigrations(string Schema, Assembly Assembly);
