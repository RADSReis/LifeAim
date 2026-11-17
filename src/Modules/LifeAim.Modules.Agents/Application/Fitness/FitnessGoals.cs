using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LifeAim.Modules.Goals.Contracts;

namespace LifeAim.Modules.Agents.Application.Fitness;

/// <summary>
/// Which goals have the fitness specialty ("meta com especialidade"): a goal about weight or about training opens the
/// weight-loss journal, and the journal belongs to it. Plain code over the goal's own fields, so it costs nothing and
/// always answers the same; the link itself is kept in the journal's profile (fitness.profiles), not in the Goals
/// module, which knows nothing about specialties.
/// </summary>
internal static partial class FitnessGoals
{
    /// <summary>A goal measured in kilos, or that talks about weight, fat or slimming.</summary>
    public static bool IsWeight(GoalDto goal) =>
        string.Equals(goal.Unit?.Trim(), "kg", StringComparison.OrdinalIgnoreCase)
        || Weight().IsMatch(Plain($"{goal.Name} {goal.Indicator}"));

    /// <summary>A goal about working out: gym, running, a sport.</summary>
    public static bool IsTraining(GoalDto goal) => Training().IsMatch(Plain($"{goal.Name} {goal.Indicator}"));

    // Lower case, no accents: "Musculação" and "musculacao" are the same word.
    private static string Plain(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    [GeneratedRegex(@"\b(emagrec\w*|perder\s+peso|peso|pesar|gordura|quilos?|kg)\b")]
    private static partial Regex Weight();

    [GeneratedRegex(@"\b(academia|trein\w*|muscula\w*|malhar|corr(er|ida|idas)|caminha\w*|exercicio\w*|natacao|nadar|pedal\w*|bike|bicicleta|crossfit|pilates)\b")]
    private static partial Regex Training();
}
