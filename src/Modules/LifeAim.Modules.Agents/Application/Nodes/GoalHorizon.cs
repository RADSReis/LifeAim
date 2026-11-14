namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// The goals' time horizon depends on the month (UX "Definição do período dos objetivos"). Plain code:
/// no reason to pay an AI call for a rule this simple.
/// </summary>
internal static class GoalHorizon
{
    // LifeAim's only user lives in Brazil; this becomes a user setting when there are more.
    private static readonly TimeZoneInfo UserTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static string Question(TimeProvider timeProvider)
    {
        var month = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), UserTimeZone).Month;

        return month switch
        {
            1 or 2 => "Quais objetivos você quer alcançar ao longo deste ano?",
            12 => "Quer definir as suas metas de ano novo?",
            _ => "Quer definir objetivos para o fim deste ano ou para algum mês específico?",
        };
    }
}
