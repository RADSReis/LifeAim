namespace LifeAim.Modules.Fitness.Contracts;

/// <summary>
/// The day something belongs to in the journal. People count a ceia after midnight as part of the day before ("00:02 —
/// ceia" closes the 13th, it does not open the 14th), so the food day only turns at <see cref="Start"/>.
/// </summary>
public static class FoodDay
{
    /// <summary>04:00: before it, the night still belongs to the day before.</summary>
    public static readonly TimeOnly Start = new(4, 0);

    /// <summary>The food day of a moment in the user's local time.</summary>
    public static DateOnly Of(DateTime local) =>
        DateOnly.FromDateTime(TimeOnly.FromDateTime(local) < Start ? local.AddDays(-1) : local);

    /// <summary>True for a time after midnight that still belongs to the day before (sorted after the evening).</summary>
    public static bool IsLateNight(TimeOnly time) => time < Start;
}
