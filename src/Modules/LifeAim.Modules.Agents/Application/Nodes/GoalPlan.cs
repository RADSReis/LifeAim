using System.Globalization;
using System.Text;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// One routine as the AI returns it. Text where a fixed vocabulary is easier for the model ("weekly",
/// "morning", "09:30"); the code checks every value and drops what does not make sense.
/// </summary>
internal sealed record ExtractedRoutine(
    string? Activity,
    string? Frequency,
    int? Interval,
    List<int>? DaysOfWeek,
    int? TimesPerPeriod,
    string? TimeOfDay,
    string? DayPeriod,
    decimal? Amount,
    string? Unit,
    int? DurationMinutes);

/// <summary>A goal's plan: the AI's rules for routines, the conversion to the contract and how Pippin shows it.</summary>
internal static class GoalPlan
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly string[] WeekdayNames =
        ["segunda", "terça", "quarta", "quinta", "sexta", "sábado", "domingo"];

    /// <summary>How the AI must break a plan into routines. Part of the detailing prompt.</summary>
    public const string RoutineRules =
        """
        routines: o plano quebrado em atividades recorrentes (uma entrada por atividade diferente). Cada uma tem:
        - activity: o que fazer, curto, com verbo ("ler", "ir à academia", "correr").
        - frequency: "daily", "weekly" ou "monthly".
        - interval: a cada quantos períodos (1 = toda semana; 2 = semana sim, semana não). Normalmente 1.
        - daysOfWeek: dias fixos, só com "weekly", em números ISO: 1 = segunda, 2 = terça, 3 = quarta, 4 = quinta,
          5 = sexta, 6 = sábado, 7 = domingo ("seg, qua e sex" → [1, 3, 5]; "dias úteis" → [1, 2, 3, 4, 5]).
          Lista vazia quando os dias não são fixos.
        - timesPerPeriod: quantas vezes por período quando os dias NÃO são fixos ("3 vezes por semana" → 3);
          null quando há daysOfWeek.
        - timeOfDay: horário exato "HH:mm", só se o usuário disse um horário ("às 9h30" → "09:30"); senão null.
        - dayPeriod: "morning", "afternoon", "evening" ou "night" quando ele disse só o período ("de manhã" →
          "morning"; "à noite" → "night"); null se disse o horário exato ou nada.
          Nunca transforme um período em horário exato.
        - amount e unit: quanto por vez ("10 páginas" → 10 e "páginas"); null se não houver.
        - durationMinutes: duração de cada vez em minutos ("meia hora" → 30); null se não houver.
        """;

    /// <summary>Keeps only routines that make sense; fixes what can be fixed (fixed days imply weekly).</summary>
    public static IReadOnlyList<RoutineInput> ToRoutines(IEnumerable<ExtractedRoutine>? routines, RoutineSource source) =>
        (routines ?? [])
            .Select(r => ToRoutine(r, source))
            .OfType<RoutineInput>()
            .ToList();

    private static RoutineInput? ToRoutine(ExtractedRoutine routine, RoutineSource source)
    {
        var activity = routine.Activity?.Trim();
        if (string.IsNullOrEmpty(activity) || activity.Length > 100)
        {
            return null;
        }

        var days = (routine.DaysOfWeek ?? []).Where(day => day is >= 1 and <= 7).Distinct().Order().ToList();
        RoutineFrequency? parsed = routine.Frequency?.Trim().ToLowerInvariant() switch
        {
            "daily" => RoutineFrequency.Daily,
            "weekly" => RoutineFrequency.Weekly,
            "monthly" => RoutineFrequency.Monthly,
            _ => null,
        };
        var frequency = days.Count > 0 ? RoutineFrequency.Weekly : parsed;
        if (frequency is null)
        {
            return null;
        }

        DayPeriod? period = routine.DayPeriod?.Trim().ToLowerInvariant() switch
        {
            "morning" => DayPeriod.Morning,
            "afternoon" => DayPeriod.Afternoon,
            "evening" => DayPeriod.Evening,
            "night" => DayPeriod.Night,
            _ => null,
        };
        TimeOnly? time = TimeOnly.TryParseExact(routine.TimeOfDay?.Trim(), "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var exact) ? exact : null;
        var unit = string.IsNullOrWhiteSpace(routine.Unit) ? null : routine.Unit.Trim();

        return new RoutineInput(
            activity,
            frequency.Value,
            source,
            Interval: routine.Interval is >= 1 ? routine.Interval.Value : 1,
            DaysOfWeek: days,
            TimesPerPeriod: days.Count == 0 && routine.TimesPerPeriod is >= 1 ? routine.TimesPerPeriod : null,
            TimeOfDay: time,
            DayPeriod: time is null ? period : null,
            Amount: routine.Amount is > 0 ? routine.Amount : null,
            Unit: unit is { Length: <= 30 } ? unit : null,
            DurationMinutes: routine.DurationMinutes is >= 1 ? routine.DurationMinutes : null);
    }

    /// <summary>A routine runs from today until the goal's due date, unless the user said otherwise.</summary>
    public static IReadOnlyList<RoutineInput> WithDates(IEnumerable<RoutineInput> routines, DateOnly? dueDate, DateOnly today)
    {
        DateOnly? end = dueDate is { } date && date >= today ? date : null;
        return routines.Select(r => r with { StartsOn = r.StartsOn ?? today, EndsOn = r.EndsOn ?? end }).ToList();
    }

    /// <summary>A routine back in the shape the AI reads, so a correction starts from what the user saw.</summary>
    public static ExtractedRoutine ToExtracted(RoutineInput routine) =>
        new(
            routine.Activity,
            routine.Frequency.ToString().ToLowerInvariant(),
            routine.Interval,
            [.. routine.DaysOfWeek ?? []],
            routine.TimesPerPeriod,
            routine.TimeOfDay?.ToString("HH:mm", CultureInfo.InvariantCulture),
            routine.DayPeriod?.ToString().ToLowerInvariant(),
            routine.Amount,
            routine.Unit,
            routine.DurationMinutes);

    /// <summary>
    /// What Pippin understood (or proposes) for the goal in focus, and the confirmation question:
    /// "Entendi assim o plano de **Ler 2 livros**: … Está certo?".
    /// </summary>
    public static string Confirmation(GoalsDraft draft)
    {
        var goal = draft.Goals[0];
        var plan = draft.Plan;
        var text = new StringBuilder()
            .Append(plan?.Source == RoutineSource.Pippin ? "Minha proposta de plano para **" : "Entendi assim o plano de **")
            .Append(goal.Name).Append("**:");

        if (goal.StartValue is { } start)
        {
            text.Append("\n- **Hoje:** ").Append(GoalExtraction.Number(start));
            if (goal.Unit is not null)
            {
                text.Append(' ').Append(goal.Unit);
            }
        }

        var target = new[]
        {
            goal.TargetValue is { } value ? GoalExtraction.Number(value) + (goal.Unit is null ? "" : " " + goal.Unit) : null,
            goal.DueDate is { } due ? "até " + due.ToString("dd/MM/yyyy", PtBr) : null,
        }.Where(part => part is not null);
        if (target.Any())
        {
            text.Append("\n- **Meta:** ").Append(string.Join(" · ", target));
        }

        if (plan is { Routines.Count: > 0 })
        {
            foreach (var routine in plan.Routines)
            {
                text.Append("\n- **Rotina:** ").Append(Describe(routine));
            }
        }
        else
        {
            text.Append("\n- **Rotina:** a combinar");
        }

        if (goal.Description is not null)
        {
            text.Append("\n- **Contexto:** ").Append(goal.Description);
        }

        if (draft.Assumptions is { Count: > 0 } assumptions)
        {
            text.Append("\n\n💭 Supus: ").Append(string.Join("; ", assumptions)).Append('.');
        }

        text.Append("\n\nEstá certo? Responda **sim** para salvar, ou me diga o que ajustar.");
        return text.ToString();
    }

    /// <summary>"ler — segunda, quarta e sexta · de manhã · 10 páginas".</summary>
    public static string Describe(RoutineInput routine)
    {
        var parts = new List<string> { When(routine) };

        if (routine.TimeOfDay is { } time)
        {
            parts.Add("às " + time.ToString("HH:mm", PtBr));
        }
        else if (routine.DayPeriod is { } period)
        {
            parts.Add(period switch
            {
                DayPeriod.Morning => "de manhã",
                DayPeriod.Afternoon => "à tarde",
                DayPeriod.Evening => "no fim da tarde",
                _ => "à noite",
            });
        }

        if (routine.Amount is { } amount)
        {
            parts.Add(GoalExtraction.Number(amount) + (routine.Unit is null ? "" : " " + routine.Unit));
        }

        if (routine.DurationMinutes is { } minutes)
        {
            parts.Add($"{minutes} min");
        }

        return $"{routine.Activity} — {string.Join(" · ", parts)}";
    }

    private static string When(RoutineInput routine)
    {
        var every = routine.Interval > 1;
        var days = routine.DaysOfWeek ?? [];
        return routine.Frequency switch
        {
            RoutineFrequency.Daily => every ? $"a cada {routine.Interval} dias" : "todo dia",
            RoutineFrequency.Weekly when days.Count > 0 =>
                (every ? $"a cada {routine.Interval} semanas, " : "") + Join(days.Select(d => WeekdayNames[d - 1])),
            RoutineFrequency.Weekly =>
                $"{routine.TimesPerPeriod ?? 1}x por semana" + (every ? $", a cada {routine.Interval} semanas" : ""),
            _ => $"{routine.TimesPerPeriod ?? 1}x por mês" + (every ? $", a cada {routine.Interval} meses" : ""),
        };
    }

    /// <summary>"segunda, quarta e sexta".</summary>
    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} e {list[^1]}";
    }

    /// <summary>
    /// The general summary after detailing (Funcionalidade 1, decision 4): name, due date and what is measured,
    /// "A ser determinado" for what is missing, and the closing sentence of the Notion page.
    /// </summary>
    public static string Summary(IEnumerable<GoalDto> goals)
    {
        var text = new StringBuilder("**Resumo das suas metas:**");
        var number = 1;
        foreach (var goal in goals)
        {
            var measured = GoalExtraction.Values(GoalExtraction.FromSaved(goal));
            var indicator = goal.Indicator is null && measured is null
                ? "indicador: A ser determinado"
                : string.Join(": ", new[] { goal.Indicator, measured }.Where(part => part is not null));
            var due = goal.DueDate is { } date ? "até " + date.ToString("dd/MM/yyyy", PtBr) : "prazo: A ser determinado";

            text.Append('\n').Append(number++).Append(". **").Append(goal.Name).Append("** — ")
                .Append(indicator).Append(" · ").Append(due);
        }

        text.Append("\n\nEsse é o resumo das metas. Se quiser as informações completas sobre alguma delas, é só falar.");
        return text.ToString();
    }
}
