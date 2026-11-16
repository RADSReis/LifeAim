using System.ComponentModel;
using System.Globalization;
using System.Text;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Agent;

/// <summary>
/// The tools the goals agent can call, created for ONE message of ONE user. Each public method becomes a tool
/// (AIFunctionFactory reads its name, its [Description] and its parameters to build the JSON schema the model sees).
/// The model only picks the tool and fills the arguments; this code checks them and does the work through the
/// other modules' contracts. It never sees ids: goals are referred to by their number in the list of the prompt,
/// and the user id comes from the context, so the model cannot touch someone else's goals.
/// Every tool returns plain text for the model to read. Problems are returned as text too ("Erro: ..."), so the
/// model can tell the user or try again, instead of the whole reply failing.
/// </summary>
internal sealed class GoalTools(
    ConversationContext context,
    IGoalService goals,
    IProgressService progress,
    ICheckInService checkIns,
    TimeProvider timeProvider)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>How many recent reports get_goal shows.</summary>
    private const int RecentEntries = 10;

    /// <summary>
    /// The goal the user asked to cancel. The tool does not cancel: destructive actions need an explicit "sim"
    /// (Funcionalidade 1, decision 5), so the workflow asks it in the next stage, in code.
    /// </summary>
    public GoalDto? CancellationRequested { get; private set; }

    /// <summary>Why the user wants to end it, when they said (kept with the goal's history).</summary>
    public string? CancellationReason { get; private set; }

    /// <summary>
    /// The goal the user asked to replace, and the new one (Funcionalidade 6). Ending a goal needs the same explicit
    /// "sim" as a cancellation, so the workflow asks it in code too.
    /// </summary>
    public GoalReplacement? ReplacementRequested { get; private set; }

    /// <summary>Names of the tools called, in order, for the log.</summary>
    public List<string> Called { get; } = [];

    public IList<AITool> All() =>
    [
        AIFunctionFactory.Create(GetGoalAsync, "get_goal",
            "Mostra tudo o que se sabe de uma meta salva: campos, plano (rotinas), as palavras do usuário sobre o plano " +
            "e os progressos registrados. Chame antes de responder perguntas sobre os detalhes ou o andamento de uma meta."),
        AIFunctionFactory.Create(UpdateGoalAsync, "update_goal",
            "Altera campos de uma meta salva (nome, descrição, indicador, prazo, valor inicial, valor alvo, unidade). " +
            "Informe só os campos que mudam; os omitidos (null) ficam como estão."),
        AIFunctionFactory.Create(ReplaceGoalPlanAsync, "replace_goal_plan",
            "Substitui o plano inteiro de uma meta salva por estas rotinas (as antigas são apagadas). " +
            "Passe TODAS as rotinas que devem ficar, inclusive as que não mudaram."),
        AIFunctionFactory.Create(RequestGoalCancellation, "request_goal_cancellation",
            "Pede o encerramento (cancelamento) de uma meta salva que não faz mais sentido. NÃO cancela: o sistema pede " +
            "a confirmação explícita ao usuário. O histórico da meta fica guardado."),
        AIFunctionFactory.Create(RequestGoalReplacement, "request_goal_replacement",
            "Pede a substituição de uma meta salva por uma nova (\"troca a corrida por natação\"). NÃO substitui: o " +
            "sistema pede a confirmação explícita. A meta antiga fica no histórico; a nova começa sem plano."),
        AIFunctionFactory.Create(PauseGoalAsync, "pause_goal",
            "Pausa uma meta salva por um tempo: ela sai do acompanhamento e dos resumos até voltar, e o plano fica " +
            "guardado. Também serve para mudar o dia de volta de uma meta já pausada (use pausedNumber)."),
        AIFunctionFactory.Create(ResumeGoalAsync, "resume_goal",
            "Retoma uma meta pausada (número da lista de metas pausadas), com o plano que ela tinha."),
        AIFunctionFactory.Create(GoalHistoryAsync, "goal_history",
            "Mostra as metas pausadas, encerradas e substituídas, com datas, motivos e o progresso registrado em cada uma."),
        AIFunctionFactory.Create(RecordProgressAsync, "record_progress",
            "Registra algo que o usuário fez por uma meta (\"hoje li 10 páginas\", \"corri 5 km\", \"pesei 92 kg\"), " +
            "mesmo que a unidade não seja a do indicador principal da meta."),
    ];

    public async Task<string> GetGoalAsync(
        [Description("O número da meta na lista de metas salvas.")] int goalNumber,
        CancellationToken cancellationToken)
    {
        Called.Add("get_goal");
        if (await FindAsync(goalNumber, cancellationToken) is not { } goal)
        {
            return NotFound(goalNumber);
        }

        var routines = (await goals.ListRoutinesAsync(context.UserId, cancellationToken))
            .Where(r => r.GoalId == goal.Id)
            .ToList();
        var entries = await progress.ListByGoalAsync(context.UserId, goal.Id, 200, cancellationToken);

        var text = new StringBuilder($"Meta {goalNumber}: {goal.Name}");
        Line(text, "Descrição", goal.Description);
        Line(text, "Indicador", goal.Indicator);
        Line(text, "Valor inicial", Value(goal.StartValue, goal.Unit));
        Line(text, "Valor alvo", Value(goal.TargetValue, goal.Unit));
        Line(text, "Prazo", goal.DueDate?.ToString("dd/MM/yyyy", PtBr) ?? "A ser determinado");
        Line(text, "Plano nas palavras do usuário", goal.UserPlan);

        text.Append("\nRotinas:");
        if (routines.Count == 0)
        {
            text.Append(" nenhuma (plano a combinar)");
        }

        foreach (var routine in routines)
        {
            text.Append("\n- ").Append(GoalPlan.Describe(ToInput(routine)))
                .Append(routine.Source == RoutineSource.Pippin ? " (proposta do Pippin)" : "");
        }

        text.Append('\n').Append(ProgressSummary(entries));
        return text.ToString();
    }

    public async Task<string> UpdateGoalAsync(
        [Description("O número da meta na lista de metas salvas.")] int goalNumber,
        [Description("Novo nome curto, ou null para manter.")] string? name = null,
        [Description("Nova descrição (contexto da meta), ou null para manter.")] string? description = null,
        [Description("O que é medido, em palavras (\"peso corporal\"), ou null para manter.")] string? indicator = null,
        [Description("Novo prazo no formato yyyy-MM-dd, ou null para manter.")] string? dueDate = null,
        [Description("Novo valor inicial (onde o usuário estava), ou null para manter.")] decimal? startValue = null,
        [Description("Novo valor alvo, ou null para manter.")] decimal? targetValue = null,
        [Description("Nova unidade dos valores (\"kg\", \"livros\"), ou null para manter.")] string? unit = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("update_goal");
        if (await FindAsync(goalNumber, cancellationToken) is not { } goal)
        {
            return NotFound(goalNumber);
        }

        DateOnly? due = goal.DueDate;
        if (dueDate is not null)
        {
            due = GoalExtraction.ParseDate(dueDate);
            if (due is null)
            {
                return $"Erro: \"{dueDate}\" não é uma data no formato yyyy-MM-dd. Nada foi alterado.";
            }
        }

        try
        {
            var updated = await goals.UpdateAsync(
                new UpdateGoalRequest(
                    goal.Id, context.UserId,
                    name ?? goal.Name, description ?? goal.Description, indicator ?? goal.Indicator, due,
                    startValue ?? goal.StartValue, targetValue ?? goal.TargetValue, unit ?? goal.Unit, goal.UserPlan),
                cancellationToken);

            return updated is null
                ? NotFound(goalNumber)
                : $"Meta {goalNumber} alterada. Agora: {updated.Name} — " +
                  $"{GoalExtraction.Values(GoalExtraction.FromSaved(updated)) ?? "sem valores"} · " +
                  $"prazo {updated.DueDate?.ToString("dd/MM/yyyy", PtBr) ?? "A ser determinado"}.";
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi alterado.";
        }
    }

    public async Task<string> ReplaceGoalPlanAsync(
        [Description("O número da meta na lista de metas salvas.")] int goalNumber,
        [Description("Todas as rotinas do novo plano.")] List<ExtractedRoutine> routines,
        [Description("true se o plano é uma proposta do Pippin que o usuário aprovou; false se o usuário o descreveu.")]
        bool proposedByPippin,
        CancellationToken cancellationToken = default)
    {
        Called.Add("replace_goal_plan");
        if (await FindAsync(goalNumber, cancellationToken) is not { } goal)
        {
            return NotFound(goalNumber);
        }

        var source = proposedByPippin ? RoutineSource.Pippin : RoutineSource.User;
        var valid = GoalPlan.ToRoutines(routines, source);
        if (valid.Count == 0)
        {
            return "Erro: nenhuma rotina válida (cada uma precisa de activity e frequency daily, weekly ou monthly). " +
                   "O plano não foi alterado.";
        }

        var dated = GoalPlan.WithDates(valid, goal.DueDate, GoalHorizon.Today(timeProvider));
        if (await goals.ReplaceRoutinesAsync(goal.Id, context.UserId, dated, cancellationToken) is null)
        {
            return NotFound(goalNumber);
        }

        // The plan in the user's own words (goals.user_plan) keeps growing with every message that changed it.
        if (!proposedByPippin)
        {
            var words = string.IsNullOrWhiteSpace(goal.UserPlan)
                ? context.IncomingText.Trim()
                : $"{goal.UserPlan}\n{context.IncomingText.Trim()}";
            await goals.UpdateAsync(
                new UpdateGoalRequest(
                    goal.Id, context.UserId, goal.Name, goal.Description, goal.Indicator, goal.DueDate,
                    goal.StartValue, goal.TargetValue, goal.Unit, words),
                cancellationToken);
        }

        return $"Plano da meta {goalNumber} salvo:\n" + string.Join("\n", dated.Select(r => "- " + GoalPlan.Describe(r)));
    }

    public async Task<string> RequestGoalCancellation(
        [Description("O número da meta na lista de metas salvas; null se for uma meta pausada.")] int? goalNumber = null,
        [Description("O motivo, nas palavras do usuário, se ele disse; senão null.")] string? reason = null,
        [Description("O número da meta na lista de metas pausadas, se for uma delas; senão null.")] int? pausedNumber = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("request_goal_cancellation");
        if (await FindAnyAsync(goalNumber, pausedNumber, cancellationToken) is not { } goal)
        {
            return NotFoundAny;
        }

        (CancellationRequested, CancellationReason, ReplacementRequested) = (goal, Blank(reason), null);
        return "Pedido anotado. O sistema vai pedir a confirmação ao usuário: não diga que a meta foi cancelada " +
               "e não faça você a pergunta de confirmação.";
    }

    public async Task<string> RequestGoalReplacement(
        [Description("Nome curto da meta nova.")] string newName,
        [Description("O número da meta que sai, na lista de metas salvas; null se for uma meta pausada.")] int? goalNumber = null,
        [Description("O número da meta que sai, na lista de metas pausadas, se for uma delas; senão null.")] int? pausedNumber = null,
        [Description("Descrição da meta nova (contexto), ou null.")] string? newDescription = null,
        [Description("Prazo da meta nova, yyyy-MM-dd, ou null (então vale o prazo da antiga).")] string? newDueDate = null,
        [Description("O motivo da troca, nas palavras do usuário, se ele disse; senão null.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("request_goal_replacement");
        if (await FindAnyAsync(goalNumber, pausedNumber, cancellationToken) is not { } goal)
        {
            return NotFoundAny;
        }

        if (Blank(newName) is not { } name)
        {
            return "Erro: a meta nova precisa de um nome. Nada foi pedido.";
        }

        var due = goal.DueDate;
        if (newDueDate is not null && (due = GoalExtraction.ParseDate(newDueDate)) is null)
        {
            return $"Erro: \"{newDueDate}\" não é uma data no formato yyyy-MM-dd. Nada foi pedido.";
        }

        ReplacementRequested = new GoalReplacement(goal, new GoalDraft(name, Blank(newDescription), null, null, null, null, due), Blank(reason));
        CancellationRequested = null;
        return "Pedido anotado. O sistema vai pedir a confirmação ao usuário: não diga que a meta foi substituída " +
               "e não faça você a pergunta de confirmação.";
    }

    public async Task<string> PauseGoalAsync(
        [Description("O número da meta na lista de metas salvas; null se for uma meta já pausada.")] int? goalNumber = null,
        [Description("O número da meta na lista de metas pausadas, para mudar o dia de volta; senão null.")] int? pausedNumber = null,
        [Description("O dia em que a pausa termina, yyyy-MM-dd, se o usuário disse (\"por duas semanas\" → calcule); senão null.")]
        string? resumeOn = null,
        [Description("O motivo da pausa, nas palavras do usuário, se ele disse; senão null.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("pause_goal");
        if (await FindAnyAsync(goalNumber, pausedNumber, cancellationToken) is not { } goal)
        {
            return NotFoundAny;
        }

        DateOnly? day = null;
        if (resumeOn is not null)
        {
            day = GoalExtraction.ParseDate(resumeOn);
            if (day is null || day <= GoalHorizon.Today(timeProvider))
            {
                return $"Erro: \"{resumeOn}\" não é um dia futuro no formato yyyy-MM-dd. Nada foi pausado.";
            }
        }

        if (!await goals.PauseAsync(goal.Id, context.UserId, day, Blank(reason), cancellationToken))
        {
            return "Erro: não deu para pausar essa meta. Nada foi alterado.";
        }

        return $"Meta \"{goal.Name}\" pausada" +
               (day is { } d ? $" até {d.ToString("dd/MM/yyyy", PtBr)} (nesse dia o Pippin pergunta se quer retomar)." : " sem data para voltar.") +
               " O plano e o histórico ficam guardados; ela sai do acompanhamento diário e dos resumos enquanto estiver pausada.";
    }

    public async Task<string> ResumeGoalAsync(
        [Description("O número da meta na lista de metas pausadas.")] int pausedNumber,
        CancellationToken cancellationToken = default)
    {
        Called.Add("resume_goal");
        if (await FindPausedAsync(pausedNumber, cancellationToken) is not { } goal)
        {
            return $"Erro: não existe a meta pausada {pausedNumber}. Confira o número na lista de metas pausadas.";
        }

        if (!await goals.ResumeAsync(goal.Id, context.UserId, cancellationToken))
        {
            return "Erro: não deu para retomar essa meta. Nada foi alterado.";
        }

        var routines = (await goals.ListRoutinesAsync(context.UserId, cancellationToken)).Where(r => r.GoalId == goal.Id).ToList();
        return $"Meta \"{goal.Name}\" retomada. " + (routines.Count == 0
            ? "Ela não tem plano ainda."
            : "O plano continua:\n" + string.Join("\n", routines.Select(r => "- " + GoalPlan.Describe(GoalPlan.FromSaved(r)))));
    }

    public async Task<string> GoalHistoryAsync(CancellationToken cancellationToken = default)
    {
        Called.Add("goal_history");
        var history = await goals.ListHistoryAsync(context.UserId, cancellationToken);
        if (history.Count == 0)
        {
            return "Nenhuma meta pausada, encerrada ou substituída.";
        }

        var all = (await goals.ListAsync(context.UserId, cancellationToken)).Concat(history).ToList();
        var text = new StringBuilder();
        foreach (var goal in history)
        {
            var since = goal.StatusChangedAt is { } at ? GoalHorizon.Local(at).ToString("dd/MM/yyyy", PtBr) : "?";
            text.Append("\n- ").Append(goal.Name).Append(": ").Append(goal.Status switch
            {
                GoalStatus.Paused => $"pausada desde {since}" +
                                     (goal.ResumeOn is { } back ? $", volta em {back.ToString("dd/MM/yyyy", PtBr)}" : ", sem data para voltar"),
                GoalStatus.Replaced => $"substituída em {since} por \"{all.FirstOrDefault(g => g.Id == goal.ReplacedBy)?.Name ?? "outra meta"}\"",
                _ => $"encerrada em {since}",
            });
            if (goal.StatusReason is { } reason)
            {
                text.Append(" (motivo: ").Append(reason).Append(')');
            }

            var entries = await progress.ListByGoalAsync(context.UserId, goal.Id, 200, cancellationToken);
            text.Append(". ").Append(entries.Count == 0
                ? "Sem progresso registrado."
                : $"{entries.Count} registro(s) de progresso" + Totals(entries) + ".");
        }

        return "Histórico (o caminho percorrido, mesmo sem a meta concluída):" + text;
    }

    private static string Totals(IReadOnlyList<ProgressEntryDto> entries)
    {
        var totals = entries.Where(e => e.Amount is not null && e.Unit is not null)
            .GroupBy(e => e.Unit!)
            .Select(g => $"{GoalExtraction.Number(g.Sum(e => e.Amount!.Value))} {g.Key}")
            .ToList();
        return totals.Count == 0 ? "" : $", somando {string.Join(" e ", totals)}";
    }

    private const string NotFoundAny =
        "Erro: não encontrei essa meta. Confira o número na lista de metas salvas (goalNumber) ou pausadas (pausedNumber).";

    private async Task<GoalDto?> FindAnyAsync(int? goalNumber, int? pausedNumber, CancellationToken cancellationToken) =>
        goalNumber is { } n ? await FindAsync(n, cancellationToken)
        : pausedNumber is { } p ? await FindPausedAsync(p, cancellationToken)
        : null;

    // The paused goals as numbered in the prompt: the user's paused goals, oldest first.
    private async Task<GoalDto?> FindPausedAsync(int pausedNumber, CancellationToken cancellationToken)
    {
        var paused = (await goals.ListHistoryAsync(context.UserId, cancellationToken)).Where(g => g.Status == GoalStatus.Paused).ToList();
        return pausedNumber >= 1 && pausedNumber <= paused.Count ? paused[pausedNumber - 1] : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task<string> RecordProgressAsync(
        [Description("O número da meta na lista de metas salvas.")] int goalNumber,
        [Description("O que foi feito, em poucas palavras, na terceira pessoa (\"leu 10 páginas\").")] string note,
        [Description("Quanto foi feito (10), se o usuário disse um número; senão null.")] decimal? amount = null,
        [Description("A unidade da quantidade (\"páginas\", \"km\", \"kg\"); null se não houver quantidade.")] string? unit = null,
        [Description("O dia em que aconteceu, yyyy-MM-dd (\"ontem\" → a data de ontem); null para hoje.")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        Called.Add("record_progress");
        if (await FindAsync(goalNumber, cancellationToken) is not { } goal)
        {
            return NotFound(goalNumber);
        }

        var today = GoalHorizon.Today(timeProvider);
        var day = today;
        if (date is not null)
        {
            if (GoalExtraction.ParseDate(date) is not { } parsed)
            {
                return $"Erro: \"{date}\" não é uma data no formato yyyy-MM-dd. Nada foi registrado.";
            }

            day = parsed;
        }

        if (day > today)
        {
            return "Erro: não dá para registrar progresso de um dia que ainda não chegou. Nada foi registrado.";
        }

        try
        {
            var entry = await progress.RecordAsync(
                new RecordProgressRequest(context.UserId, goal.Id, day, note, amount is > 0 ? amount : null, unit),
                cancellationToken);

            // A follow-up still waiting for that day (Funcionalidade 3) is answered by this report.
            await checkIns.MarkDoneForGoalAsync(context.UserId, goal.Id, day, entry.Id, cancellationToken);
            var entries = await progress.ListByGoalAsync(context.UserId, goal.Id, 200, cancellationToken);

            return $"Registrado na meta {goalNumber} ({goal.Name}), em {entry.OccurredOn.ToString("dd/MM/yyyy", PtBr)}: " +
                   $"{entry.Note}{(entry.Amount is { } a ? $" ({Value(a, entry.Unit)})" : "")}.\n" +
                   ProgressSummary(entries);
        }
        catch (ArgumentException ex)
        {
            return $"Erro: {ex.Message} Nada foi registrado.";
        }
    }

    // The numbers are the ones in the prompt's list, which is the list of active goals, oldest first.
    // Read again (not from the context) so a goal changed earlier in this same message is seen as it is now.
    private async Task<GoalDto?> FindAsync(int goalNumber, CancellationToken cancellationToken)
    {
        var saved = await goals.ListAsync(context.UserId, cancellationToken);
        return goalNumber >= 1 && goalNumber <= saved.Count ? saved[goalNumber - 1] : null;
    }

    private static string NotFound(int goalNumber) =>
        $"Erro: não existe a meta {goalNumber} na lista de metas salvas. Confira o número na lista.";

    /// <summary>
    /// What the model needs to talk about progress: the totals (so it does not add numbers itself) and the
    /// latest reports. "nos últimos 7 dias" feeds messages like "você já leu 120 páginas em duas semanas".
    /// </summary>
    private string ProgressSummary(IReadOnlyList<ProgressEntryDto> entries)
    {
        if (entries.Count == 0)
        {
            return "Progresso: nenhum registro ainda.";
        }

        var today = GoalHorizon.Today(timeProvider);
        var text = new StringBuilder($"Progresso: {entries.Count} registro(s), o primeiro em ")
            .Append(entries[^1].OccurredOn.ToString("dd/MM/yyyy", PtBr)).Append('.');

        foreach (var byUnit in entries.Where(e => e.Amount is not null).GroupBy(e => e.Unit ?? ""))
        {
            var lastWeek = byUnit.Where(e => e.OccurredOn > today.AddDays(-7)).Sum(e => e.Amount!.Value);
            text.Append("\nTotal em ").Append(byUnit.Key == "" ? "(sem unidade)" : byUnit.Key).Append(": ")
                .Append(GoalExtraction.Number(byUnit.Sum(e => e.Amount!.Value)))
                .Append(" (nos últimos 7 dias: ").Append(GoalExtraction.Number(lastWeek)).Append(')');
        }

        text.Append("\nÚltimos registros (do mais novo para o mais antigo):");
        foreach (var entry in entries.Take(RecentEntries))
        {
            text.Append("\n- ").Append(entry.OccurredOn.ToString("dd/MM/yyyy", PtBr)).Append(": ").Append(entry.Note);
            if (entry.Amount is { } amount)
            {
                text.Append(" (").Append(Value(amount, entry.Unit)).Append(')');
            }
        }

        return text.ToString();
    }

    private static string? Value(decimal? value, string? unit) =>
        value is { } v ? GoalExtraction.Number(v) + (unit is null ? "" : " " + unit) : null;

    private static void Line(StringBuilder text, string label, string? value)
    {
        if (value is not null)
        {
            text.Append('\n').Append(label).Append(": ").Append(value);
        }
    }

    private static RoutineInput ToInput(RoutineDto r) =>
        new(r.Activity, r.Frequency, r.Source, r.Interval, r.DaysOfWeek, r.TimesPerPeriod, r.TimeOfDay, r.DayPeriod,
            r.Amount, r.Unit, r.DurationMinutes, r.StartsOn, r.EndsOn);
}
