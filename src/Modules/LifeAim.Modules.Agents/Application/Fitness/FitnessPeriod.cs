using System.Globalization;
using System.Text;
using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Modules.Agents.Application.Fitness;

/// <summary>
/// A period of the journal in a few counted facts, for the monthly summary (Funcionalidade 4): how many days were
/// recorded, the averages of what was eaten, the workouts, and how the weight and the waist moved. Plain code: the AI
/// that writes the summary only repeats these numbers. Days without a record are not days of failure, so every
/// average is over the days that have the data, and says how many they are.
/// </summary>
internal static class FitnessPeriod
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Fewer recorded days than this and the period says too little to be summarized.</summary>
    public const int MinDays = 3;

    /// <summary>How many weigh-ins at each end of the period make its "start" and "end" weight (daily weight swings).</summary>
    public const int WeightSpan = 7;

    /// <summary>The facts as text, or null when the period has fewer than <see cref="MinDays"/> recorded days.</summary>
    public static string? Summary(IReadOnlyList<FitnessDayDto> days, FitnessProfileDto? profile)
    {
        if (days.Count < MinDays)
        {
            return null;
        }

        var text = new StringBuilder($"- Dias com algum registro no diário: {days.Count}.");

        var fed = days.Where(d => d.Meals.Count > 0).ToList();
        if (fed.Count > 0)
        {
            text.Append($"\n- Alimentação registrada em {fed.Count} dia(s): média de ")
                .Append(FitnessText.Range(fed.Average(d => d.KcalMin), fed.Average(d => d.KcalMax), "kcal"))
                .Append(" e proteína ")
                .Append(FitnessText.Range(fed.Average(d => d.ProteinMinG), fed.Average(d => d.ProteinMaxG), "g"))
                .Append(" por dia.");

            if (profile?.DailyEnergyKcal is { } energy)
            {
                // A day counts as a deficit (or a surplus) only when the whole range of its estimate agrees.
                var deficit = fed.Count(d => energy + d.ExerciseKcalMin - d.KcalMax > 0);
                var surplus = fed.Count(d => energy + d.ExerciseKcalMax - d.KcalMin < 0);
                text.Append($"\n- Desses dias, {deficit} fecharam em déficit, {surplus} em superávit e {fed.Count - deficit - surplus} perto da manutenção")
                    .Append($" (gasto estimado de {FitnessText.Number(energy)} kcal sem treino).");
            }

            if (profile?.ProteinTargetG is { } proteinTarget)
            {
                text.Append($"\n- Meta de proteína ({proteinTarget} g) alcançada em {fed.Count(d => d.ProteinMaxG >= proteinTarget)} desses dias.");
            }
        }

        var trained = days.Where(d => d.Exercises.Count > 0).ToList();
        text.Append($"\n- Dias com treino registrado: {trained.Count}");
        if (trained.Any(d => d.ExerciseKcalMax > 0))
        {
            text.Append($" (gasto somado: {FitnessText.Range(trained.Sum(d => d.ExerciseKcalMin), trained.Sum(d => d.ExerciseKcalMax), "kcal")})");
        }

        text.Append('.');

        var weights = days.Where(d => d.Body?.WeightKg is not null).Select(d => (d.Day, Kg: d.Body!.WeightKg!.Value)).ToList();
        if (weights.Count == 1)
        {
            text.Append($"\n- Uma pesagem: {FitnessText.Exact(weights[0].Kg)} kg em {weights[0].Day.ToString("dd/MM", PtBr)}.");
        }
        else if (weights.Count > 1)
        {
            // With few weigh-ins the two ends would overlap: each end takes at most half of them.
            var span = Math.Min(WeightSpan, weights.Count / 2);
            var (start, end) = (weights.Take(span).Average(w => w.Kg), weights.TakeLast(span).Average(w => w.Kg));
            text.Append($"\n- Peso: {weights.Count} pesagens. Média das {span} primeiras: {FitnessText.Exact(Math.Round(start, 2))} kg; ")
                .Append($"das {span} últimas: {FitnessText.Exact(Math.Round(end, 2))} kg ({Signed(Math.Round(end - start, 2))} kg). ")
                .Append($"Menor do período: {FitnessText.Exact(weights.Min(w => w.Kg))} kg.");
            if (profile?.TargetWeightKg is { } target)
            {
                text.Append($" Peso alvo: {FitnessText.Exact(target)} kg.");
            }
        }

        var waists = days.Where(d => d.Body?.WaistCm is not null).Select(d => d.Body!.WaistCm!.Value).ToList();
        if (waists.Count > 1)
        {
            text.Append($"\n- Circunferência abdominal: de {FitnessText.Exact(waists[0])} para {FitnessText.Exact(waists[^1])} cm ({Signed(waists[^1] - waists[0])} cm).");
        }

        return text.ToString();
    }

    private static string Signed(decimal value) => (value > 0 ? "+" : "") + value.ToString("0.##", PtBr);
}
