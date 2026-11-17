using System.Globalization;
using System.Text;
using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Modules.Agents.Application.Fitness;

/// <summary>
/// The journal written as text for the model to read (in the prompt and in the tools' results). Every total is added
/// up here, in code: the model only repeats the numbers, it never sums a day by itself.
/// </summary>
internal static class FitnessText
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    // Written here because the culture data abbreviates differently on each system ("dom" or "dom.").
    private static readonly string[] Weekdays = ["dom", "seg", "ter", "qua", "qui", "sex", "sáb"];

    /// <summary>One day in full: body, numbered meals with their items, numbered exercises, notes and totals.</summary>
    public static string Day(FitnessDayDto day, FitnessProfileDto? profile)
    {
        var text = new StringBuilder($"Dia {day.Day.ToString("dd/MM/yyyy (dddd)", PtBr)}");
        if (day.IsEmpty)
        {
            return text.Append(": nada registrado.").ToString();
        }

        if (day.Body is { } body)
        {
            text.Append('\n').Append(Body(body));
        }

        // The ids are time-ordered (UUID v7): the greatest is the meal recorded last, which is the one a correction
        // without a name is about ("foi às 17:15", "na verdade foram 3").
        var latest = day.Meals.Count > 1 ? day.Meals.MaxBy(m => m.Id.ToString())?.Id : null;

        text.Append("\nRefeições:").Append(day.Meals.Count == 0 ? " nenhuma registrada" : "");
        for (var i = 0; i < day.Meals.Count; i++)
        {
            var meal = day.Meals[i];
            text.Append('\n').Append(i + 1).Append(". ")
                .Append(meal.EatenAt is { } at ? at.ToString("HH:mm", PtBr) : "horário não informado")
                .Append(" — ").Append(meal.Name).Append(": ")
                .Append(Range(meal.KcalMin, meal.KcalMax, "kcal")).Append(" · proteína ")
                .Append(Range(meal.ProteinMinG, meal.ProteinMaxG, "g"))
                .Append(meal.Id == latest ? " ← a última registrada" : "");
            foreach (var item in meal.Items)
            {
                text.Append("\n   - ").Append(Item(item));
            }
        }

        if (day.Exercises.Count > 0)
        {
            text.Append("\nExercícios:");
            for (var i = 0; i < day.Exercises.Count; i++)
            {
                text.Append('\n').Append(i + 1).Append(". ").Append(Exercise(day.Exercises[i]));
            }
        }

        if (day.Notes.Count > 0)
        {
            text.Append("\nObservações do dia: ").Append(string.Join("; ", day.Notes));
        }

        return text.Append('\n').Append(Totals(day, profile)).ToString();
    }

    /// <summary>The day's totals and, when the daily expenditure is known, the balance.</summary>
    public static string Totals(FitnessDayDto day, FitnessProfileDto? profile)
    {
        var text = new StringBuilder("Totais do dia: ");
        text.Append(day.Meals.Count == 0
            ? "nada ingerido registrado"
            : $"ingerido {Range(day.KcalMin, day.KcalMax, "kcal")} · proteína {Range(day.ProteinMinG, day.ProteinMaxG, "g")}");

        var carbs = day.Meals.SelectMany(m => m.Items).Sum(i => i.CarbsG ?? 0);
        var fat = day.Meals.SelectMany(m => m.Items).Sum(i => i.FatG ?? 0);
        if (carbs > 0 || fat > 0)
        {
            text.Append($" · carboidratos ~{Number(carbs)} g · gorduras ~{Number(fat)} g (só dos itens em que foram informados)");
        }

        if (day.Exercises.Count > 0)
        {
            text.Append(" · gasto com exercício ").Append(day.Exercises.All(e => e.KcalMin is null)
                ? "não estimado"
                : Range(day.ExerciseKcalMin, day.ExerciseKcalMax, "kcal"));
        }

        if (day.Meals.Count == 0)
        {
            return text.Append('.').ToString();
        }

        if (profile?.CalorieTargetKcal is { } target)
        {
            text.Append($"\nMeta de calorias do dia: {Number(target)} kcal → ")
                .Append(Remaining(target - day.KcalMax, target - day.KcalMin));
        }

        if (profile?.DailyEnergyKcal is { } energy)
        {
            // Spent minus eaten: the doubt of both ranges adds up.
            var (low, high) = (energy + day.ExerciseKcalMin - day.KcalMax, energy + day.ExerciseKcalMax - day.KcalMin);
            text.Append($"\nGasto estimado do dia: {Number(energy)} kcal sem treino")
                .Append(day.ExerciseKcalMax > 0 ? $" + exercício = {Range(energy + day.ExerciseKcalMin, energy + day.ExerciseKcalMax, "kcal")}" : "")
                .Append(" → se o dia terminasse agora: ").Append(Balance(low, high))
                .Append(" (só vale como saldo do dia depois da última refeição)");
        }

        return text.ToString();
    }

    /// <summary>One line per day, for the history ("12/07 dom: peso 94,7 kg · ingerido ~2.300–2.450 kcal · …").</summary>
    public static string DayLine(FitnessDayDto day, FitnessProfileDto? profile)
    {
        var parts = new List<string>();
        if (day.Body?.WeightKg is { } weight)
        {
            parts.Add($"peso {Exact(weight)} kg");
        }

        if (day.Body?.WaistCm is { } waist)
        {
            parts.Add($"abdominal {Exact(waist)} cm");
        }

        if (day.Body?.FatPercent is { } fat)
        {
            parts.Add($"gordura {Exact(fat)}%");
        }

        if (day.Meals.Count > 0)
        {
            parts.Add($"ingerido {Range(day.KcalMin, day.KcalMax, "kcal")} em {day.Meals.Count} refeição(ões)");
            parts.Add($"proteína {Range(day.ProteinMinG, day.ProteinMaxG, "g")}");
        }

        if (day.Exercises.Count > 0)
        {
            var activities = string.Join(" + ", day.Exercises.Select(e =>
                e.Activity + (e.DurationMinutes is { } minutes ? $" {minutes} min" : "")));
            parts.Add($"treino: {activities}" + (day.Exercises.Any(e => e.KcalMin is not null)
                ? $" ({Range(day.ExerciseKcalMin, day.ExerciseKcalMax, "kcal")})"
                : ""));
        }

        if (day.Meals.Count > 0 && profile?.DailyEnergyKcal is { } energy)
        {
            parts.Add("saldo " + Balance(energy + day.ExerciseKcalMin - day.KcalMax, energy + day.ExerciseKcalMax - day.KcalMin));
        }

        if (day.Notes.Count > 0)
        {
            parts.Add("obs.: " + string.Join("; ", day.Notes));
        }

        if (day.Body?.Note is { } note)
        {
            parts.Add("obs. do peso: " + note);
        }

        return $"{day.Day.ToString("dd/MM", PtBr)} {Weekdays[(int)day.Day.DayOfWeek]}: {string.Join(" · ", parts)}";
    }

    /// <summary>How the weight is moving: the latest, the 7-day averages (daily weight swings with water), the extremes.</summary>
    public static string? WeightTrend(IReadOnlyList<FitnessDayDto> days, DateOnly today, FitnessProfileDto? profile)
    {
        var weights = days.Where(d => d.Body?.WeightKg is not null).Select(d => (d.Day, Kg: d.Body!.WeightKg!.Value)).ToList();
        if (weights.Count == 0)
        {
            return null;
        }

        var (lastDay, last) = weights[^1];
        var text = new StringBuilder($"Último peso: {Exact(last)} kg em {lastDay.ToString("dd/MM", PtBr)}");
        if (weights.Count > 1)
        {
            var (firstDay, first) = weights[0];
            text.Append($" · primeiro do período: {Exact(first)} kg em {firstDay.ToString("dd/MM", PtBr)} ({Signed(last - first)} kg)");
            text.Append($" · menor: {Exact(weights.Min(w => w.Kg))} kg · maior: {Exact(weights.Max(w => w.Kg))} kg");
        }

        var thisWeek = weights.Where(w => w.Day > today.AddDays(-7)).Select(w => w.Kg).ToList();
        var weekBefore = weights.Where(w => w.Day > today.AddDays(-14) && w.Day <= today.AddDays(-7)).Select(w => w.Kg).ToList();
        if (thisWeek.Count >= 2)
        {
            text.Append($"\nMédia dos últimos 7 dias: {Exact(thisWeek.Average())} kg ({thisWeek.Count} pesagens)");
            if (weekBefore.Count >= 2)
            {
                text.Append($" · média dos 7 dias anteriores: {Exact(weekBefore.Average())} kg " +
                            $"({Signed(thisWeek.Average() - weekBefore.Average())} kg de uma semana para a outra)");
            }
        }

        if (profile?.TargetWeightKg is { } target)
        {
            text.Append($"\nPeso alvo: {Exact(target)} kg (faltam {Exact(Math.Max(0, last - target))} kg)");
        }

        return text.ToString();
    }

    public static string Body(BodyRecordDto body)
    {
        var parts = new List<string>();
        Add(parts, "peso", body.WeightKg, " kg");
        Add(parts, "abdominal", body.WaistCm, " cm");
        Add(parts, "quadril", body.HipCm, " cm");
        Add(parts, "peitoral", body.ChestCm, " cm");
        Add(parts, "músculo esquelético", body.MuscleKg, " kg");
        Add(parts, "massa gorda", body.FatKg, " kg");
        Add(parts, "água corporal", body.WaterKg, " kg");
        Add(parts, "gordura corporal", body.FatPercent, "%");
        Add(parts, "IMC", body.Bmi, "");
        Add(parts, "metabolismo basal", body.BmrKcal, " kcal");
        if (body.Note is { } note)
        {
            parts.Add("obs.: " + note);
        }

        return "Corpo: " + string.Join(" · ", parts);
    }

    public static string Exercise(ExerciseDto exercise) =>
        (exercise.DoneAt is { } at ? at.ToString("HH:mm", PtBr) + " — " : "") + exercise.Activity
        + (exercise.DurationMinutes is { } minutes ? $", {minutes} min" : "")
        + (exercise.KcalMin is { } min && exercise.KcalMax is { } max
            ? $": {Range(min, max, "kcal")} ({(exercise.Measured ? "medido" : "estimativa")})"
            : "");

    public static string Food(FoodDto food) =>
        $"{food.Name} — {food.Serving}: {Number(food.Kcal)} kcal"
        + (food.ProteinG is { } protein ? $", {Number(protein)} g proteína" : "")
        + (food.CarbsG is { } carbs ? $", {Number(carbs)} g carboidratos" : "")
        + (food.FatG is { } fat ? $", {Number(fat)} g gorduras" : "");

    public static string Reminder(ReminderDto reminder) =>
        reminder.Kind switch
        {
            ReminderKind.WeighIn => "pesagem",
            ReminderKind.NoRecord => "aviso de dia sem refeição registrada",
            ReminderKind.DayClose => "fechamento do dia",
            _ => $"\"{reminder.Text}\"",
        }
        + $" às {reminder.At.ToString("HH:mm", PtBr)}"
        + (reminder.DoneToday ? " (feito hoje)" : reminder.SentToday ? " (já enviado hoje)" : "");

    public static string ReferenceFood(ReferenceFoodDto food) =>
        $"{food.Name} — por 100 g: {Number(food.KcalPer100g)} kcal"
        + (food.ProteinG is { } protein ? $", {Number(protein)} g proteína" : "")
        + (food.CarbsG is { } carbs ? $", {Number(carbs)} g carboidratos" : "")
        + (food.FatG is { } fat ? $", {Number(fat)} g gorduras" : "")
        + (food.FiberG is { } fiber ? $", {Number(fiber)} g fibra" : "");

    public static string Profile(FitnessProfileDto? profile, DateOnly today)
    {
        if (profile is null)
        {
            return "ainda não preenchido";
        }

        var parts = new List<string>();
        Add(parts, "altura", profile.HeightCm, " cm");
        if (profile.BirthYear is { } year)
        {
            parts.Add($"nascido em {year} (~{today.Year - year} anos)");
        }

        if (profile.Sex is { } sex)
        {
            parts.Add(sex == Sex.Female ? "sexo feminino" : "sexo masculino");
        }

        Add(parts, "peso alvo", profile.TargetWeightKg, " kg");
        Add(parts, "gasto diário estimado sem treino", profile.DailyEnergyKcal, " kcal");
        Add(parts, "meta de calorias", profile.CalorieTargetKcal, " kcal/dia");
        Add(parts, "meta de proteína", profile.ProteinTargetG, " g/dia");
        if (profile.Notes is { } notes)
        {
            parts.Add("observações: " + notes);
        }

        return parts.Count == 0 ? "ainda não preenchido" : string.Join(" · ", parts);
    }

    private static string Item(MealItemDto item) =>
        item.Description
        + (item.Quantity is { } quantity ? $" {Number(quantity)}{(item.Unit is { } unit ? " " + unit : "")}" : "")
        + $": {Range(item.KcalMin, item.KcalMax, "kcal")}, proteína {Range(item.ProteinMinG, item.ProteinMaxG, "g")}"
        + (item.CarbsG is { } carbs ? $", carboidratos {Number(carbs)} g" : "")
        + (item.FatG is { } fat ? $", gorduras {Number(fat)} g" : "")
        + item.Source switch
        {
            NutritionSource.User => " (valores do usuário)",
            NutritionSource.FoodTable => " (tabela do usuário)",
            NutritionSource.ReferenceTable => " (TACO)",
            _ => " (estimativa)",
        };

    /// <summary>"510 kcal" for an exact value, "~510–540 kcal" for a range.</summary>
    public static string Range(decimal min, decimal max, string unit) =>
        Math.Round(min) == Math.Round(max) ? $"{Number(min)} {unit}" : $"~{Number(min)}–{Number(max)} {unit}";

    // Positive = spent more than eaten.
    private static string Balance(decimal low, decimal high) =>
        low >= 0 ? $"déficit de {Range(low, high, "kcal")}"
        : high <= 0 ? $"superávit de {Range(-high, -low, "kcal")}"
        : $"perto da manutenção (entre superávit de {Number(-low)} e déficit de {Number(high)} kcal)";

    private static string Remaining(decimal low, decimal high) =>
        low >= 0 ? $"restam {Range(low, high, "kcal")}"
        : high <= 0 ? $"passou {Range(-high, -low, "kcal")} da meta"
        : $"no limite da meta (entre {Number(-low)} kcal acima e {Number(high)} kcal restantes)";

    private static void Add(List<string> parts, string label, decimal? value, string suffix)
    {
        if (value is { } v)
        {
            parts.Add($"{label} {v.ToString("0.##", PtBr)}{suffix}");
        }
    }

    /// <summary>Whole numbers with the thousands dot ("2.050"); the journal's doubt is larger than a decimal place.</summary>
    public static string Number(decimal value) =>
        Math.Abs(value) >= 100 ? Math.Round(value).ToString("#,0", PtBr) : value.ToString("0.#", PtBr);

    /// <summary>Weights and measures keep their decimals ("94,25").</summary>
    public static string Exact(decimal value) => value.ToString("0.##", PtBr);

    private static string Signed(decimal value) => (value > 0 ? "+" : "") + value.ToString("0.##", PtBr);
}
