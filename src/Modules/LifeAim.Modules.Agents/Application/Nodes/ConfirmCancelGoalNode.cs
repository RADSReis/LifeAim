using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// ⑥ The goals agent asked to cancel a goal, and Pippin asked "Tem certeza?" (Funcionalidade 1, decision 5:
/// destructive actions need an explicit confirmation). A node, not the agent, because what happens next is a rule
/// the code must guarantee: nothing is cancelled without a clear "sim" to this exact question. The AI only
/// classifies the answer; the code cancels (keeping the history) and goes back to where the conversation was.
/// </summary>
internal sealed class ConfirmCancelGoalNode(IChatClient chatClient, IGoalService goals, GoalDetailing detailing) : INode
{
    public Stage Stage => Stage.ConfirmCancelGoal;

    /// <summary>The question, as in the Notion example: the goal's name and an explicit "sim".</summary>
    public static string Question(string goalName) =>
        $"Tem certeza que a meta **{goalName}** deve ser cancelada? 🤔 O histórico dela fica guardado, mas ela sai " +
        "da sua lista. Responda **sim** para cancelar ou **não** para mantê-la.";

    /// <summary>The same explicit "sim" to replace a goal by another (Funcionalidade 6): the old one is ended.</summary>
    public static string ReplaceQuestion(string goalName, string newGoalName) =>
        $"Tem certeza que a meta **{goalName}** deve ser substituída por **{newGoalName}**? 🔄 A antiga sai da lista, mas " +
        "o histórico dela fica guardado. Responda **sim** para trocar ou **não** para manter como está.";

    public string? PendingQuestion(ConversationContext context) =>
        Goal(context) is not { } goal ? null
        : context.Draft?.Replacement is { } replacement ? ReplaceQuestion(goal.Name, replacement.Name)
        : Question(goal.Name);

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var draft = context.Draft ?? GoalsDraft.Empty(null);
        var returnStage = draft.ReturnStage ?? Stage.CollectGoals;
        var replacement = draft.Replacement;
        var back = draft with { CancelGoalId = null, ReturnStage = null, CancelReason = null, Replacement = null };

        if (Goal(context) is not { } goal)
        {
            context.Draft = back; // the goal is gone already: nothing to confirm
            return NodeResult.Resume("Essa meta já não está mais na sua lista.", returnStage);
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(PendingQuestion(context)!)),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<CancelAnswer>(messages, cancellationToken: cancellationToken);
        var decision = response.TryGetResult(out var answer) ? answer.Decision?.Trim().ToLowerInvariant() : null;

        switch (decision)
        {
            case Decisions.Confirm when replacement is not null:
                context.Draft = back;
                return await ReplaceAsync(context, goal, replacement, draft.CancelReason, returnStage, cancellationToken);

            case Decisions.Confirm:
                context.Draft = back;
                var cancelled = await goals.CancelAsync(goal.Id, context.UserId, draft.CancelReason, cancellationToken);
                var done = cancelled
                    ? $"Pronto, cancelei a meta **{goal.Name}**. O histórico dela continua guardado. 🗂️"
                    : "Essa meta já não está mais na sua lista.";
                return await BackAsync(context, back, returnStage, goal.Id, done, cancellationToken);

            case Decisions.Keep:
                context.Draft = back;
                return NodeResult.Resume(
                    replacement is null
                        ? $"Combinado, a meta **{goal.Name}** continua de pé! 💪"
                        : $"Combinado, nada muda: a meta **{goal.Name}** continua como está. 💪",
                    returnStage);

            case Decisions.Greeting:
                return NodeResult.Greeting; // greets back and asks again

            // Anything else is not a "sim": the goal stays, and the message is handled from where the user was.
            default:
                context.Draft = back;
                return NodeResult.NotExpectedAfter(
                    replacement is null ? $"Não cancelei a meta **{goal.Name}**." : $"Não troquei a meta **{goal.Name}**.",
                    returnStage);
        }
    }

    // The old goal is closed as replaced and the new one is saved, in one transaction. Between tasks, Pippin goes on to
    // detail the new goal (it starts without a plan); in the middle of something, that is left for later.
    private async Task<NodeResult> ReplaceAsync(
        ConversationContext context, GoalDto goal, GoalDraft replacement, string? reason, Stage returnStage,
        CancellationToken cancellationToken)
    {
        var created = await goals.ReplaceAsync(
            goal.Id,
            new CreateGoalRequest(
                context.UserId, replacement.Name, replacement.Description, replacement.Indicator, replacement.DueDate,
                replacement.StartValue, replacement.TargetValue, replacement.Unit),
            reason,
            cancellationToken);
        if (created is null)
        {
            return NodeResult.Resume("Essa meta já não está mais na sua lista.", returnStage);
        }

        var done = $"Pronto, troquei **{goal.Name}** por **{created.Name}**! 🔄 O histórico da antiga continua guardado.";
        if (returnStage == Stage.CollectGoals)
        {
            return await detailing.NextAsync(context, [created.Id], done, cancellationToken);
        }

        return await BackAsync(context, context.Draft ?? GoalsDraft.Empty(null), returnStage, goal.Id, done, cancellationToken);
    }

    // Back to where the conversation was. If it was deepening the cancelled goal, that one leaves the queue.
    private async Task<NodeResult> BackAsync(
        ConversationContext context, GoalsDraft draft, Stage returnStage, Guid cancelledId, string done,
        CancellationToken cancellationToken)
    {
        if (returnStage == Stage.DetailGoals && draft.DetailQueue is { Count: > 0 } queue && queue.Contains(cancelledId))
        {
            var rest = queue.Where(id => id != cancelledId).ToList();
            if (queue[0] == cancelledId)
            {
                return await detailing.NextAsync(context, rest, done, cancellationToken); // the next goal, or the summary
            }

            context.Draft = draft with { DetailQueue = rest };
        }

        return NodeResult.Resume(done, returnStage);
    }

    private static GoalDto? Goal(ConversationContext context) =>
        context.Draft?.CancelGoalId is { } id
            ? context.SavedGoals.Concat(context.PausedGoals).FirstOrDefault(g => g.Id == id)
            : null;

    private static string Instructions(string question) =>
        $"""
        Você ajuda o Pippin, um assistente que conversa em português sobre as metas de vida do usuário.
        O Pippin perguntou: "{question}". Classifique a resposta em decision:
        - "{Decisions.Confirm}": confirma claramente ("sim", "pode cancelar", "pode trocar", "tenho", "isso").
        - "{Decisions.Keep}": quer manter a meta ("não", "deixa", "mudei de ideia", "melhor não").
        - "{Decisions.Greeting}": é só um cumprimento ("oi", "bom dia").
        - "{Decisions.Other}": qualquer outra coisa, inclusive respostas em dúvida ("acho que sim, mas...").
        Na dúvida, use "{Decisions.Other}": cancelar ou trocar só com uma confirmação clara.
        """;

    private static class Decisions
    {
        public const string Confirm = "confirm";
        public const string Keep = "keep";
        public const string Greeting = "greeting";
        public const string Other = "other";
    }

    internal sealed record CancelAnswer(string? Decision);
}
