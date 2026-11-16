using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.FollowUp;

/// <summary>
/// Writes the follow-up message Pippin sends on his own (Funcionalidade 3, "Iniciativa e mensagens no momento
/// esperado"). The code already decided what to ask (<see cref="FollowUpPlanner"/>) and counted the history; ONE AI
/// call turns that into a short, contextual message ("Você começou ontem. Agora é hora de construir consistência…"),
/// so it does not read like a generic notification. If the AI answer cannot be read, a plain message goes instead.
/// </summary>
internal sealed class CheckInWriter(IChatClient chatClient)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    // Accents as they are ("ir à academia"), not à: the prompt is read by the model, never put in HTML.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const int MaxMessageLength = 1200;

    public async Task<string> WriteAsync(
        FollowUpPlan plan,
        FollowUpInput input,
        string? userName,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> messages = [new(ChatRole.System, Instructions(plan, input, userName))];
        var response = await chatClient.GetResponseAsync<WriterAnswer>(messages, cancellationToken: cancellationToken);

        var text = response.TryGetResult(out var answer) ? answer.Message?.Trim() : null;
        if (string.IsNullOrEmpty(text))
        {
            return Plain(plan);
        }

        return text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;
    }

    /// <summary>The message without the AI: what is planned now and what is still unconfirmed.</summary>
    internal static string Plain(FollowUpPlan plan)
    {
        var lines = new List<string>();
        lines.AddRange(plan.Due.Select(d => $"Hora de {d.Routine.Activity} ({d.Goal.Name})! Bora? 💪"));
        lines.AddRange(plan.PendingToday.Select(c => $"Me conta também se conseguiu {c.Activity} hoje."));
        lines.AddRange(plan.Earlier.Select(c =>
            $"Ah, {DayWord(c.Day, plan.Today)} não consegui confirmar: conseguiu {c.Activity}? Se não deu, tudo bem."));
        return string.Join("\n", lines);
    }

    /// <summary>"hoje", "ontem" or "no dia 14/11".</summary>
    internal static string DayWord(DateOnly day, DateOnly today) =>
        day == today ? "hoje"
        : day == today.AddDays(-1) ? "ontem"
        : $"no dia {day.ToString("dd/MM", PtBr)}";

    private static string Instructions(FollowUpPlan plan, FollowUpInput input, string? userName)
    {
        var now = GoalHorizon.Local(input.UtcNow);
        var facts = new
        {
            agora = plan.Due.Select(d => new
            {
                meta = d.Goal.Name,
                atividade = d.Routine.Activity,
                quantidadePlanejada = d.Routine.Amount,
                unidade = d.Routine.Unit,
                duracaoMinutos = d.Routine.DurationMinutes,
                historico = GoalHistory.Of(input, d.Goal.Id, plan.Today),
            }),
            semRespostaHoje = plan.PendingToday.Select(c => new { meta = GoalName(input, c.GoalId), atividade = c.Activity }),
            semConfirmacaoAntes = plan.Earlier.Select(c => new
            {
                meta = GoalName(input, c.GoalId),
                atividade = c.Activity,
                quando = DayWord(c.Day, plan.Today),
            }),
        };

        return $"""
            Você é o Pippin, o assistente do LifeAim: conversa em português, de forma leve, calorosa e natural, pelo
            Discord, e acompanha as metas do usuário{(userName is null ? "" : $" ({userName})")} no dia a dia.
            Agora é {now.ToString("dddd, dd/MM/yyyy, HH:mm", PtBr)}.

            Você vai mandar uma mensagem por iniciativa própria. Os fatos (JSON):
            {JsonSerializer.Serialize(facts, Json)}

            - agora: atividades planejadas para este momento. historico traz, da meta: primeiraVez (nunca houve registro),
              diasSeguidos (dias seguidos com atividade feita até ontem), ultimos7Dias (feitas, naoFeitas, semConfirmacao)
              e semanaPassada (feitas na semana anterior, de segunda a domingo).
            - semRespostaHoje: atividades de hoje que você já perguntou e o usuário ainda não respondeu.
            - semConfirmacaoAntes: atividades de dias anteriores que ficaram sem confirmação.

            Escreva UMA mensagem curta (até 70 palavras), no estilo de chat:
            - Incentive a atividade de agora usando o contexto do histórico, sem parecer notificação genérica. Exemplos:
              primeira vez → "Hoje é o primeiro dia! Vamos começar bem?"; começou ontem → "Você começou ontem. Agora é
              hora de construir consistência."; semana boa → reconheça; semana fraca → convide a retomar, sem culpa.
            - Valorize a consistência (regularidade) mais do que o volume.
            - Se houver semRespostaHoje, peça de forma natural que ele conte também como foi ("Depois me conta também se
              conseguiu ir à academia hoje").
            - Se houver semConfirmacaoAntes, diga que você não conseguiu confirmar e pergunte se aconteceu, deixando claro
              que tudo bem se não deu ("Ontem não consegui confirmar se você foi. Acabou conseguindo?").
            - Nunca presuma que algo não foi feito só porque não houve resposta. Nada de culpa, cobrança ou pressão.
            - Não invente números, horários ou fatos que não estão acima. Use **negrito** no nome das metas, no máximo um
              emoji, sem títulos e sem listas.
            """;
    }

    private static string GoalName(FollowUpInput input, Guid goalId) =>
        input.Goals.FirstOrDefault(g => g.Id == goalId)?.Name ?? "meta";

    internal sealed record WriterAnswer(string? Message);
}

/// <summary>The numbers of one goal's recent history the AI uses for context (it never adds them up itself).</summary>
internal sealed record GoalHistory(bool PrimeiraVez, int DiasSeguidos, GoalHistory.Week Ultimos7Dias, int SemanaPassada)
{
    public static GoalHistory Of(FollowUpInput input, Guid goalId, DateOnly today)
    {
        var doneDays = FollowUpPlanner.DoneDays(input, goalId);
        var checkIns = input.CheckIns.Where(c => c.GoalId == goalId).ToList();

        var streak = 0;
        for (var day = today.AddDays(-1); doneDays.Contains(day); day = day.AddDays(-1))
        {
            streak++;
        }

        var weekAgo = today.AddDays(-7);
        var lastWeek = new Week(
            doneDays.Count(d => d >= weekAgo && d < today),
            checkIns.Count(c => c.Day >= weekAgo && c.Day < today && c.Status == CheckInStatus.Missed),
            checkIns.Count(c => c.Day >= weekAgo && c.Day < today && c.Status == CheckInStatus.Asked && !doneDays.Contains(c.Day)));

        var thisMonday = FollowUpPlanner.Monday(today);
        var previousWeek = doneDays.Count(d => d >= thisMonday.AddDays(-7) && d < thisMonday);

        return new GoalHistory(doneDays.Count == 0 && checkIns.Count == 0, streak, lastWeek, previousWeek);
    }

    internal sealed record Week(int Feitas, int NaoFeitas, int SemConfirmacao);
}
