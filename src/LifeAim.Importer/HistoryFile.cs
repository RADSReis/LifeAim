using System.Globalization;
using System.Text.RegularExpressions;
using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Importer;

/// <summary>
/// The text file the journal used to be kept in by hand, read by plain code: its three sections (what was eaten, the
/// body measurements, the nutrition table), the days of the first one, and the whole second one, which is regular
/// enough for regular expressions. What is free text (the meals, the product labels) is left to the AI extractor.
/// </summary>
internal static partial class HistoryFile
{
    public sealed record Sections(string Meals, string Body, string Foods);

    /// <summary>One day of the meals section, as written.</summary>
    public sealed record DayText(DateOnly Day, string Text);

    /// <summary>What the body section says about the person, before the daily records.</summary>
    public sealed record Preamble(decimal? HeightCm, int? AgeYears, decimal? TargetWeightKg, string? Notes);

    /// <summary>
    /// Cuts the consolidated file at its numbered section titles. A file without them is taken whole as
    /// <paramref name="only"/> ("meals", "body" or "foods"), for someone who kept three separate files.
    /// </summary>
    public static Sections Split(string text, string? only = null)
    {
        var meals = SectionTitle().Matches(text).FirstOrDefault(m => m.Groups[1].Value.Contains("ALIMENTA", StringComparison.OrdinalIgnoreCase));
        var body = SectionTitle().Matches(text).FirstOrDefault(m => m.Groups[1].Value.Contains("EMAGRECIMENTO", StringComparison.OrdinalIgnoreCase));
        var foods = SectionTitle().Matches(text).FirstOrDefault(m => m.Groups[1].Value.Contains("TABELA", StringComparison.OrdinalIgnoreCase));

        if (meals is null && body is null && foods is null)
        {
            return only switch
            {
                "body" => new Sections("", text, ""),
                "foods" => new Sections("", "", text),
                _ => new Sections(text, "", ""),
            };
        }

        var starts = new[] { meals, body, foods }.Where(m => m is not null).Select(m => m!.Index).Order().ToList();
        string Cut(Match? title) =>
            title is null ? "" : text[(title.Index + title.Length)..(starts.Where(s => s > title.Index).DefaultIfEmpty(text.Length).First())];

        return new Sections(Cut(meals), Cut(body), Cut(foods));
    }

    /// <summary>
    /// The days of the meals section. A day starts at a line like "Registro – 26/01/2026" or "📅 Resumo — 04/03"; a
    /// date without a year takes the year of the day before it (the file runs forward in time).
    /// </summary>
    public static IReadOnlyList<DayText> Days(string mealsSection, int firstYear)
    {
        var headers = DayHeader().Matches(mealsSection);
        var days = new List<DayText>();
        var year = firstYear;
        DateOnly? previous = null;

        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (header.Groups[3].Success)
            {
                year = int.Parse(header.Groups[3].Value, CultureInfo.InvariantCulture);
            }

            if (!TryDate(year, header.Groups[2].Value, header.Groups[1].Value, out var day))
            {
                continue;
            }

            // No year written and the calendar went backwards: the file crossed into the next year.
            if (!header.Groups[3].Success && previous is { } before && day < before.AddDays(-60))
            {
                day = day.AddYears(1);
                year++;
            }

            var end = i + 1 < headers.Count ? headers[i + 1].Index : mealsSection.Length;
            var text = mealsSection[header.Index..end].Trim();

            // "Registro – 30/01" followed by "Resumo — Dia 30/01": the same day written in two parts.
            if (days.Count > 0 && days[^1].Day == day)
            {
                days[^1] = days[^1] with { Text = days[^1].Text + "\n" + text };
            }
            else
            {
                days.Add(new DayText(day, text));
            }

            previous = day;
        }

        return days;
    }

    public static Preamble ReadPreamble(string bodySection)
    {
        var head = DayOfBody().Match(bodySection) is { Success: true } first ? bodySection[..first.Index] : bodySection;
        var height = Number(Height().Match(head));
        var notes = string.Join(" ", new[] { Routine().Match(head), Restrictions().Match(head) }
            .Where(m => m.Success)
            .Select(m => m.Value.Trim()));

        return new Preamble(
            height is { } h ? (h < 3 ? h * 100 : h) : null,
            Age().Match(head) is { Success: true } age ? int.Parse(age.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            Number(Target().Match(head)),
            notes.Length == 0 ? null : notes);
    }

    /// <summary>The daily records of the body section ("Dia 02/02 … Peso: 94,4 kg …"), which never write the year.</summary>
    public static IReadOnlyList<RecordBodyRequest> BodyRecords(string bodySection, Guid userId, int firstYear)
    {
        var headers = DayOfBody().Matches(bodySection);
        var records = new List<RecordBodyRequest>();
        var year = firstYear;
        DateOnly? previous = null;

        for (var i = 0; i < headers.Count; i++)
        {
            if (!TryDate(year, headers[i].Groups[2].Value, headers[i].Groups[1].Value, out var day))
            {
                continue;
            }

            if (previous is { } before && day < before.AddDays(-60))
            {
                day = day.AddYears(1);
                year++;
            }

            previous = day;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : bodySection.Length;
            var block = bodySection[(headers[i].Index + headers[i].Length)..end];

            var bmr = Bmr().Match(block);
            var record = new RecordBodyRequest(
                userId,
                day,
                WeightKg: Number(Weight().Match(block)),
                WaistCm: Number(Waist().Match(block)),
                HipCm: Number(Hip().Match(block)),
                ChestCm: Number(Chest().Match(block)),
                MuscleKg: Number(Muscle().Match(block)),
                FatKg: Number(FatMass().Match(block)),
                WaterKg: Number(Water().Match(block)),
                FatPercent: Number(FatPercent().Match(block)),
                Bmi: Number(Bmi().Match(block)),
                BmrKcal: bmr.Success ? int.Parse(bmr.Groups[1].Value.Replace(".", ""), CultureInfo.InvariantCulture) : null,
                Note: Note(block));

            // A header with nothing readable under it is not a record.
            if (record with { Note = null } != new RecordBodyRequest(userId, day))
            {
                records.Add(record);
            }
        }

        return records;
    }

    // What the person wrote about the day: "Obs.: ...", or a loose sentence ("após 2 dias de alta ingestão calórica").
    private static string? Note(string block)
    {
        var lines = block.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !KnownLine().IsMatch(l) && !l.StartsWith('=') && !l.StartsWith("- "))
            .Select(l => ObsPrefix().Replace(l, "").Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var note = string.Join(" ", lines);
        return note.Length == 0 ? null : note.Length > 500 ? note[..500] : note;
    }

    private static decimal? Number(Match match) =>
        match.Success && decimal.TryParse(
            match.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool TryDate(int year, string month, string day, out DateOnly date) =>
        DateOnly.TryParseExact(
            $"{year:0000}-{int.Parse(month, CultureInfo.InvariantCulture):00}-{int.Parse(day, CultureInfo.InvariantCulture):00}",
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    [GeneratedRegex(@"^=+\s*\r?\n\s*\d\.\s*([^\r\n]+)\r?\n=+\s*$", RegexOptions.Multiline)]
    private static partial Regex SectionTitle();

    // A day's first line: starts with "Registro" or "Resumo" (maybe after an emoji) and has a date.
    [GeneratedRegex(@"^[^\p{L}\r\n]*(?:Registro|Resumo)\b[^\r\n\d]*?(\d{1,2})/(\d{1,2})(?:/(\d{4}))?[^\r\n]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DayHeader();

    [GeneratedRegex(@"^Dia\s+(\d{1,2})/(\d{1,2})[^\r\n]*$", RegexOptions.Multiline)]
    private static partial Regex DayOfBody();

    [GeneratedRegex(@"Peso(?:\s+atual)?:\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Weight();

    [GeneratedRegex(@"Circunfer[êe]ncia\s+abdominal:\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Waist();

    [GeneratedRegex(@"Circunfer[êe]ncia\s+(?:do\s+)?quadril:\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Hip();

    [GeneratedRegex(@"Circunfer[êe]ncia\s+peitoral:\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Chest();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*kg\s+de\s+m[úu]sculo", RegexOptions.IgnoreCase)]
    private static partial Regex Muscle();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*kg\s+de\s+massa\s+gorda", RegexOptions.IgnoreCase)]
    private static partial Regex FatMass();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*kg\s+de\s+[ÁA]gua", RegexOptions.IgnoreCase)]
    private static partial Regex Water();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*%\s+de\s+Gordura", RegexOptions.IgnoreCase)]
    private static partial Regex FatPercent();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s+de\s+IMC", RegexOptions.IgnoreCase)]
    private static partial Regex Bmi();

    [GeneratedRegex(@"(\d\.?\d{3})\s*kcal\s+de\s+Taxa", RegexOptions.IgnoreCase)]
    private static partial Regex Bmr();

    [GeneratedRegex(@"Altura:\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Height();

    [GeneratedRegex(@"Idade:\s*(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex Age();

    [GeneratedRegex(@"Objetivo:[^\r\n\d]*(\d+(?:[.,]\d+)?)\s*kg", RegexOptions.IgnoreCase)]
    private static partial Regex Target();

    [GeneratedRegex(@"Rotina semanal:[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex Routine();

    [GeneratedRegex(@"Restri[çc][õo]es alimentares:[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex Restrictions();

    // Lines of a body record that are measurements or the old assistant's planning, not the person's remark.
    [GeneratedRegex(@"^(Peso|Circunfer|Bioimped|Meta corporal|Gordura alvo|Tempo estimado|\d+(?:[.,]\d+)?\s*(kg|%|de IMC)|\d\.?\d{3}\s*kcal)", RegexOptions.IgnoreCase)]
    private static partial Regex KnownLine();

    [GeneratedRegex(@"^Obs\.?:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ObsPrefix();
}
