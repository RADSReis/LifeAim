using System.Globalization;
using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Importer;

/// <summary>What to import and whether to write it.</summary>
/// <param name="Apply">False (the default) only shows what would be saved.</param>
/// <param name="Only">"body", "foods" or "meals" to import one section; null for all.</param>
/// <param name="FirstYear">The year of the first dates that do not write one.</param>
internal sealed record ImportOptions(Guid UserId, bool Apply, string? Only, int FirstYear);

/// <summary>
/// Brings the journal kept by hand into LifeAim, through the Fitness module's contract (so every value passes the same
/// checks as a conversation). A simulation by default. Safe to run again: the body records and the foods are replaced
/// by the same values, and a day that already has meals (or workouts) in the journal is left as it is.
/// </summary>
internal sealed class HistoryImporter(IFitnessJournal journal, HistoryExtractor extractor, TextWriter output)
{
    /// <summary>A day whose items add up to more than this away from the total the file states is not imported.</summary>
    public const decimal MaxDivergence = 0.15m;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public async Task<ImportReport> RunAsync(string text, ImportOptions options, CancellationToken cancellationToken)
    {
        var sections = HistoryFile.Split(text, options.Only);
        var report = new ImportReport();
        output.WriteLine(options.Apply ? "== IMPORTANDO (gravando no diário)" : "== SIMULAÇÃO (nada será gravado; use --apply para gravar)");

        if (options.Only is null or "body")
        {
            await BodyAsync(sections.Body, options, report, cancellationToken);
        }

        if (options.Only is null or "foods")
        {
            await FoodsAsync(sections.Foods, options, report, cancellationToken);
        }

        if (options.Only is null or "meals")
        {
            await MealsAsync(sections.Meals, options, report, cancellationToken);
        }

        output.WriteLine();
        output.WriteLine(
            $"Resumo: {report.BodyRecords} registro(s) corporais, {report.Foods} alimento(s), {report.Days} dia(s) de alimentação " +
            $"({report.Meals} refeições, {report.Exercises} exercícios); {report.SkippedDays.Count} dia(s) já no diário; " +
            $"{report.DivergentDays.Count} dia(s) divergentes; {report.FailedDays.Count} dia(s) ilegíveis.");
        return report;
    }

    private async Task BodyAsync(string section, ImportOptions options, ImportReport report, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return;
        }

        var preamble = HistoryFile.ReadPreamble(section);
        output.WriteLine($"\n-- Perfil: altura {preamble.HeightCm} cm, idade {preamble.AgeYears}, peso alvo {preamble.TargetWeightKg} kg");
        if (options.Apply && (preamble.HeightCm ?? preamble.TargetWeightKg ?? preamble.AgeYears) is not null)
        {
            // Only what the profile does not have yet: a later conversation knows better than an old file.
            var current = await journal.GetProfileAsync(options.UserId, cancellationToken);
            await journal.SaveProfileAsync(
                new SaveProfileRequest(
                    options.UserId,
                    HeightCm: current?.HeightCm is null ? preamble.HeightCm : null,
                    BirthYear: current?.BirthYear is null && preamble.AgeYears is { } age ? options.FirstYear - age : null,
                    TargetWeightKg: current?.TargetWeightKg is null ? preamble.TargetWeightKg : null,
                    Notes: current?.Notes is null ? preamble.Notes : null),
                cancellationToken);
        }

        output.WriteLine("\n-- Registros corporais");
        foreach (var record in HistoryFile.BodyRecords(section, options.UserId, options.FirstYear))
        {
            try
            {
                if (options.Apply)
                {
                    await journal.RecordBodyAsync(record, cancellationToken);
                }

                report.BodyRecords++;
                output.WriteLine(
                    $"{record.Day:dd/MM/yyyy}: peso {record.WeightKg} · abdominal {record.WaistCm} · quadril {record.HipCm} · " +
                    $"peitoral {record.ChestCm} · gordura {record.FatPercent}% · basal {record.BmrKcal}" +
                    (record.Note is null ? "" : $" · obs.: {record.Note}"));
            }
            catch (ArgumentException ex)
            {
                output.WriteLine($"{record.Day:dd/MM/yyyy}: RECUSADO — {ex.Message}");
            }
        }
    }

    private async Task FoodsAsync(string section, ImportOptions options, ImportReport report, CancellationToken cancellationToken)
    {
        var foods = await extractor.FoodsAsync(section, cancellationToken);
        if (foods.Count == 0)
        {
            return;
        }

        output.WriteLine("\n-- Tabela nutricional");
        foreach (var food in foods.Where(f => f is { Name: not null, Serving: not null, Kcal: not null }))
        {
            try
            {
                if (options.Apply)
                {
                    await journal.SaveFoodAsync(
                        new SaveFoodRequest(options.UserId, food.Name!, food.Serving!, food.Kcal!.Value, food.ProteinG, food.CarbsG, food.FatG),
                        cancellationToken);
                }

                report.Foods++;
                output.WriteLine($"{food.Name} — {food.Serving}: {food.Kcal} kcal, {food.ProteinG} g proteína");
            }
            catch (ArgumentException ex)
            {
                output.WriteLine($"{food.Name}: RECUSADO — {ex.Message}");
            }
        }
    }

    private async Task MealsAsync(string section, ImportOptions options, ImportReport report, CancellationToken cancellationToken)
    {
        var days = HistoryFile.Days(section, options.FirstYear);
        if (days.Count == 0)
        {
            return;
        }

        var foodTable = string.Join("\n", (await journal.ListFoodsAsync(options.UserId, cancellationToken))
            .Select(f => $"- {f.Name} — {f.Serving}: {f.Kcal} kcal, {f.ProteinG} g proteína"));

        output.WriteLine($"\n-- Alimentação: {days.Count} dia(s) no arquivo");
        foreach (var day in days)
        {
            var existing = await journal.GetDayAsync(options.UserId, day.Day, cancellationToken);
            if (existing.Meals.Count > 0 || existing.Exercises.Count > 0)
            {
                report.SkippedDays.Add(day.Day);
                output.WriteLine($"{day.Day:dd/MM/yyyy}: já tem registros no diário — mantido como está");
                continue;
            }

            if (await extractor.DayAsync(day, foodTable, cancellationToken) is not { } extracted || Meals(extracted, options.UserId, day.Day) is not { Count: > 0 } meals)
            {
                report.FailedDays.Add(day.Day);
                output.WriteLine($"{day.Day:dd/MM/yyyy}: não consegui ler nenhuma refeição — não importado");
                continue;
            }

            var (min, max) = (meals.Sum(m => m.Items.Sum(i => i.KcalMin)), meals.Sum(m => m.Items.Sum(i => i.KcalMax)));
            var line = $"{day.Day:dd/MM/yyyy}: {meals.Count} refeições, {min.ToString("N0", PtBr)}–{max.ToString("N0", PtBr)} kcal";

            // The file's own total is the check: a transcription far from it is reported, never saved.
            if (extracted is { DeclaredKcalMin: { } declaredMin, DeclaredKcalMax: { } declaredMax })
            {
                var (found, declared) = ((min + max) / 2, (declaredMin + declaredMax) / 2);
                line += $" (arquivo: {declaredMin.ToString("N0", PtBr)}–{declaredMax.ToString("N0", PtBr)})";
                if (declared > 0 && Math.Abs(found - declared) / declared > MaxDivergence)
                {
                    report.DivergentDays.Add(day.Day);
                    output.WriteLine(line + " — DIVERGENTE, não importado");
                    continue;
                }
            }
            else
            {
                line += " (arquivo sem total para conferir)";
            }

            var exercises = Exercises(extracted, options.UserId, day.Day);
            if (options.Apply)
            {
                try
                {
                    foreach (var meal in meals)
                    {
                        await journal.AddMealAsync(meal, cancellationToken);
                    }

                    foreach (var exercise in exercises)
                    {
                        await journal.AddExerciseAsync(exercise, cancellationToken);
                    }

                    foreach (var note in (extracted.Notes ?? []).Where(n => !string.IsNullOrWhiteSpace(n)))
                    {
                        await journal.AddDayNoteAsync(options.UserId, day.Day, note, cancellationToken);
                    }
                }
                catch (ArgumentException ex)
                {
                    report.FailedDays.Add(day.Day);
                    output.WriteLine($"{day.Day:dd/MM/yyyy}: RECUSADO — {ex.Message}");
                    continue;
                }
            }

            report.Days++;
            report.Meals += meals.Count;
            report.Exercises += exercises.Count;
            output.WriteLine(line + $", {exercises.Count} exercício(s)");
        }
    }

    private static List<AddMealRequest> Meals(ExtractedDay day, Guid userId, DateOnly date) =>
        (day.Meals ?? [])
        .Select(meal => new AddMealRequest(
            userId,
            date,
            Time(meal.Time),
            string.IsNullOrWhiteSpace(meal.Name) ? "Refeição" : meal.Name,
            (meal.Items ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Description))
            .Select(i => new MealItemDto(
                i.Description!, i.Quantity is > 0 ? i.Quantity : null, i.Unit, i.Kcal, i.KcalMax ?? i.Kcal, i.ProteinG, i.ProteinMaxG ?? i.ProteinG,
                null, null, NutritionSource.Estimate))
            .ToList()))
        .Where(meal => meal.Items.Count > 0)
        .ToList();

    private static List<AddExerciseRequest> Exercises(ExtractedDay day, Guid userId, DateOnly date) =>
        (day.Exercises ?? [])
        .Where(e => !string.IsNullOrWhiteSpace(e.Activity))
        .Select(e => new AddExerciseRequest(
            userId, date, Time(e.Time), e.Activity!, e.DurationMinutes is > 0 ? e.DurationMinutes : null, e.Kcal, e.KcalMax,
            e.Measured && e.Kcal is not null))
        .ToList();

    private static TimeOnly? Time(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}

/// <summary>What an import did (or, in a simulation, would do).</summary>
internal sealed class ImportReport
{
    public int BodyRecords { get; set; }
    public int Foods { get; set; }
    public int Days { get; set; }
    public int Meals { get; set; }
    public int Exercises { get; set; }

    /// <summary>Days that already had meals or workouts in the journal: left as they were.</summary>
    public List<DateOnly> SkippedDays { get; } = [];

    /// <summary>Days whose transcription is too far from the total the file states: not imported.</summary>
    public List<DateOnly> DivergentDays { get; } = [];

    /// <summary>Days nothing could be read from, or refused by the journal's checks.</summary>
    public List<DateOnly> FailedDays { get; } = [];
}
