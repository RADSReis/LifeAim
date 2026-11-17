namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// Units that measure where the user IS (a weight, a waist, a percentage), not how much was DONE. Reports of these are
/// never added up: "pesou 94 kg" twenty times is not 1880 kg. What they show is the first, the last and the change.
/// </summary>
internal static class StateUnits
{
    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        "kg", "quilo", "quilos", "kilo", "kilos", "g", "cm", "%", "bpm", "imc",
    };

    public static bool Is(string? unit) => unit is not null && Units.Contains(unit.Trim());
}
