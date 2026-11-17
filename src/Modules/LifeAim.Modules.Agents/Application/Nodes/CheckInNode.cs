using System.Globalization;
using System.Text;
using LifeAim.Modules.Agents.Application.FollowUp;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// The answer to a daily follow-up (Funcionalidade 3). ONE AI call reads what the user said about each activity asked
/// (done, with how much; not done, with the reason; or nothing about it) and writes the reply; the code records it:
/// done → a progress entry (the same record as "record_progress") and the check-in answered; not done → the check-in
/// answered with the reason. Activities the user did not mention stay open, and the conversation stays here until
/// they are answered or a new follow-up replaces them. "Planejado, realizado e não confirmado são situações diferentes".
/// </summary>
internal sealed class CheckInNode(
    IChatClient chatClient,
    IProgressService progress,
    ICheckInService checkIns,
    TimeProvider timeProvider) : INode
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>"Não feito" this many times in the last 2 weeks: Pippin looks closer and offers to adjust the plan.</summary>
    public const int RecurringMisses = 3;

    private const int MissWindowDays = 14;
    private const int HistoryInPrompt = 6;

    public Stage Stage => Stage.CheckIn;

    public string? PendingQuestion(ConversationContext context)
    {
        var items = context.Draft?.CheckIns ?? [];
        if (items.Count == 0)
        {
            return null;
        }

        var today = GoalHorizon.Today(timeProvider);
        var list = string.Join(", ", items.Select(i => $"{i.Activity} ({CheckInWriter.DayWord(i.Day, today)})"));
        return items.Count == 1
            ? $"Me conta: conseguiu {items[0].Activity} {CheckInWriter.DayWord(items[0].Day, today)}?"
            : $"Me conta como foi: {list}?";
    }

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var today = GoalHorizon.Today(timeProvider);
        var draft = context.Draft ?? GoalsDraft.Empty(null);

        // Only the ones still open: one may have been answered meanwhile ("hoje li 10 páginas" through the goals agent).
        var asked = draft.CheckIns ?? [];
        var recent = await checkIns.ListAsync(context.UserId, today.AddDays(-MissWindowDays), today, cancellationToken);
        // A goal paused or ended meanwhile (Funcionalidade 6) is not asked about any more: its check-in stays unconfirmed.
        var open = asked
            .Where(i => recent.Any(c => c.Id == i.CheckInId && c.Status == CheckInStatus.Asked))
            .Where(i => context.SavedGoals.Any(g => g.Id == i.GoalId))
            .ToList();

        if (open.Count == 0)
        {
            // Nothing left to ask: the message is handled as if the follow-up were over.
            context.Draft = draft with { CheckIns = null };
            return NodeResult.NotExpectedAfter(null, Stage.CollectGoals);
        }

        List<ChatMessage> messages = [new(ChatRole.System, Instructions(context, open, recent, today))];
        messages.AddRange(context.History.TakeLast(HistoryInPrompt).Select(turn =>
            new ChatMessage(turn.Role == ConversationRole.User ? ChatRole.User : ChatRole.Assistant, turn.Content)));
        messages.Add(new ChatMessage(ChatRole.User, context.IncomingText));

        var response = await chatClient.GetResponseAsync<CheckInAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected;
        }

        // Journal data (what was eaten, the weight, a workout told with its numbers) goes to the fitness agent, which
        // records the workout in the journal AND as progress of its goal, answering this follow-up on the way.
        if (answer.Decision?.Trim().ToLowerInvariant() == "fitness")
        {
            return NodeResult.Fitness;
        }

        var outcomes = (answer.Items ?? [])
            .Where(i => i.Number is { } n && n >= 1 && n <= open.Count)
            .Where(i => i.Outcome?.Trim().ToLowerInvariant() is "done" or "not_done")
            .DistinctBy(i => i.Number)
            .ToList();

        if (outcomes.Count == 0)
        {
            return answer.Decision?.Trim().ToLowerInvariant() == "greeting" ? NodeResult.Greeting : NodeResult.NotExpected;
        }

        var answered = new HashSet<Guid>();
        foreach (var outcome in outcomes)
        {
            var item = open[outcome.Number!.Value - 1];
            if (await RecordAsync(context.UserId, item, outcome, cancellationToken))
            {
                answered.Add(item.CheckInId);
            }
        }

        var remaining = open.Where(i => !answered.Contains(i.CheckInId)).ToList();
        context.Draft = draft with { CheckIns = remaining.Count == 0 ? null : remaining };

        var reply = string.IsNullOrWhiteSpace(answer.Reply) ? "Anotado! ✅" : answer.Reply.Trim();
        return NodeResult.Answer(reply, remaining.Count == 0 ? Stage.CollectGoals : null);
    }

    private async Task<bool> RecordAsync(
        Guid userId, CheckInItem item, CheckInOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Outcome!.Trim().ToLowerInvariant() == "not_done")
        {
            return await checkIns.MarkMissedAsync(item.CheckInId, userId, outcome.Reason, cancellationToken);
        }

        // The same record "record_progress" makes: the note always, the amount only when the user said a number.
        var amount = outcome.Amount is > 0 ? outcome.Amount : null;
        var note = string.IsNullOrWhiteSpace(outcome.Note) ? item.Activity : outcome.Note.Trim();
        var entry = await progress.RecordAsync(
            new RecordProgressRequest(userId, item.GoalId, item.Day, note, amount, amount is null ? null : outcome.Unit ?? item.Unit),
            cancellationToken);
        return await checkIns.MarkDoneAsync(item.CheckInId, userId, entry.Id, cancellationToken);
    }

    private string Instructions(
        ConversationContext context, IReadOnlyList<CheckInItem> open, IReadOnlyList<CheckInDto> recent, DateOnly today)
    {
        var list = new StringBuilder();
        for (var i = 0; i < open.Count; i++)
        {
            var item = open[i];
            var misses = recent.Count(c => c.GoalId == item.GoalId && c.Status == CheckInStatus.Missed);
            list.Append(i + 1).Append(". ").Append(item.Activity)
                .Append(" — meta **").Append(item.GoalName).Append("**, ")
                .Append(CheckInWriter.DayWord(item.Day, today))
                .Append(item.Amount is { } amount ? $", planejado {GoalExtraction.Number(amount)} {item.Unit}".TrimEnd() : "")
                .Append(", não feita ").Append(misses).Append(" vez(es) nos últimos 14 dias")
                .Append('\n');
        }

        return $"""
            Você é o Pippin, o assistente do LifeAim: conversa em português, de forma leve, calorosa e natural, pelo
            Discord{(context.UserName is { } name ? $", com {name}" : "")}. Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.

            Você perguntou ao usuário como foram estas atividades planejadas:
            {list.ToString().TrimEnd()}

            Leia a resposta dele e preencha:
            - items: para cada atividade de que ele falou, number (o número acima) e outcome:
              "done" (fez; amount e unit se ele disse quanto, e note com o que fez em poucas palavras, na terceira pessoa,
              como "leu 15 páginas") ou "not_done" (não fez; reason com o motivo nas palavras dele, se disse).
              Não inclua atividades de que ele não falou: elas continuam sem confirmação, o que é diferente de não feita.
            - decision: "report" se falou de alguma atividade; "greeting" se foi só um cumprimento; "fitness" se a
              mensagem traz dados do diário de emagrecimento (o que comeu ou vai comer, calorias, peso, medidas, fome,
              dúvida de alimentação, ou um treino contado com detalhes como duração de cada exercício ou calorias
              gastas: "30 min de musculação e 30 de elíptico, 488 kcal"); "other" se é sobre outro assunto. Com
              "fitness" ou "other", items fica vazio e reply null. Um simples "fui" ou "não fui" é "report".
            - reply: a sua resposta, curta (até 60 palavras), no estilo de chat:
              - Feito: comemore e valorize a consistência. Se fez bem mais que o planejado (2x ou mais), elogie primeiro e
                lembre com leveza, em uma frase, que distribuir o esforço ao longo dos dias costuma funcionar melhor para
                criar o hábito do que concentrar tudo de uma vez. Não cite estudos.
              - Não feito: acolha, sem culpa e sem interrogatório; imprevistos acontecem; incentive a próxima vez. Só se
                a contagem de "não feita" acima for {RecurringMisses - 1} ou mais (esta seria a {RecurringMisses}ª), pergunte
                com cuidado o que está atrapalhando e ofereça ajustar o plano (frequência, horário ou quantidade).
              - Se ele não falou de alguma das atividades, pergunte sobre ela no fim, numa frase.
              Use **negrito** no nome das metas, no máximo um emoji, sem títulos e sem listas. Não invente números.
            """;
    }

    internal sealed record CheckInAnswer(string? Decision, List<CheckInOutcome>? Items, string? Reply);

    internal sealed record CheckInOutcome(int? Number, string? Outcome, decimal? Amount, string? Unit, string? Note, string? Reason);
}
