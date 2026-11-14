using System.Globalization;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// ⑤ Detailing the saved goals that are not plannable yet, one at a time. Pippin showed a proposal for the
/// goal in focus (what it assumed included); the AI classifies the answer and, for a correction, returns the
/// updated goal. The code takes the edge: confirm → updates the goal and goes to the next one; skip → next
/// one without changing it; change → the proposal again; anything else → greeting or router.
/// </summary>
internal sealed class DetailGoalsNode(
    IChatClient chatClient,
    IGoalService goals,
    GoalDetailing detailing,
    TimeProvider timeProvider) : INode
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Stage Stage => Stage.DetailGoals;

    public string? PendingQuestion(ConversationContext context) =>
        context.Draft is { Goals.Count: > 0 } draft ? GoalExtraction.Proposal(draft) : null;

    // Coming from the classifier ("vamos falar da meta dos livros"): that goal first, then the rest of the queue.
    // The message itself is not an answer to a proposal, so it is not run through RunAsync.
    public async Task<NodeResult> EnterAsync(ConversationContext context, Guid? goalId, CancellationToken cancellationToken)
    {
        if (goalId is not { } id)
        {
            return NodeResult.NotExpected;
        }

        var rest = context.Draft?.DetailQueue?.Where(queued => queued != id) ?? [];
        return await detailing.NextAsync(context, [id, .. rest], "Claro!", cancellationToken);
    }

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var draft = context.Draft;
        if (draft is not { DetailQueue.Count: > 0, Goals.Count: > 0 })
        {
            return await detailing.NextAsync(context, [], "Não tenho nenhuma meta para detalhar agora.", cancellationToken);
        }

        var queue = draft.DetailQueue;
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(draft)),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<DetailAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected;
        }

        var decision = answer.Decision?.Trim().ToLowerInvariant();
        var proposal = GoalExtraction.ToDrafts(answer.Goal is null ? [] : [answer.Goal], draft.Horizon).FirstOrDefault()
            ?? draft.Goals[0];
        var openDoubts = GoalExtraction.ToDoubts(answer.Doubts);

        // The same rule as ConfirmGoals: never save while a doubt is open, nor in the message that answers one.
        var needsChoice = decision == Decisions.Confirm && openDoubts.Count > 0;
        if (decision == Decisions.Confirm && (draft.Doubts.Count > 0 || openDoubts.Count > 0))
        {
            decision = Decisions.Change;
        }

        switch (decision)
        {
            case Decisions.Confirm:
                await goals.UpdateAsync(
                    new UpdateGoalRequest(
                        queue[0], context.UserId, proposal.Name, proposal.Description, proposal.Indicator,
                        proposal.DueDate, proposal.StartValue, proposal.TargetValue, proposal.Unit),
                    cancellationToken);
                return await detailing.NextAsync(context, queue.Skip(1), $"Salvei **{proposal.Name}**! ✅", cancellationToken);

            case Decisions.Skip:
                // The saved name, not the proposed one: the goal stays as it was.
                var savedName = context.SavedGoals.FirstOrDefault(g => g.Id == queue[0])?.Name ?? draft.Goals[0].Name;
                return await detailing.NextAsync(
                    context, queue.Skip(1), $"Tudo bem, deixei **{savedName}** como estava.", cancellationToken);

            case Decisions.Change:
                context.Draft = draft with
                {
                    Goals = [proposal],
                    Doubts = openDoubts,
                    Assumptions = GoalExtraction.CleanAssumptions(answer.Assumptions),
                };
                var text = GoalExtraction.Proposal(context.Draft);
                return NodeResult.Answer(needsChoice ? $"Preciso que você escolha uma das opções antes de salvar.\n\n{text}" : text);

            case Decisions.Greeting:
                return NodeResult.Greeting;

            default:
                return NodeResult.NotExpected; // "other" → the router, which repeats the proposal
        }
    }

    private string Instructions(GoalsDraft draft)
    {
        var today = GoalHorizon.Today(timeProvider);
        var goal = draft.Goals[0];
        var shown = new ExtractedGoal(
            goal.Name, goal.Description, goal.Indicator, goal.StartValue, goal.TargetValue, goal.Unit,
            goal.DueDate?.ToString("yyyy-MM-dd"));

        return $"""
            Você ajuda o Pippin, um assistente que conversa em português para cadastrar as metas de vida do usuário.
            Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.{(draft.Horizon is { } h ? $" O período combinado vai até {h:yyyy-MM-dd}." : "")}

            O Pippin está detalhando uma meta já salva e mostrou esta proposta (JSON):
            {JsonSerializer.Serialize(shown, Json)}
            O que ele supôs: {(draft.Assumptions is { Count: > 0 } a ? string.Join(" | ", a) : "nada")}
            Dúvidas abertas, perguntadas como escolha (JSON; o usuário viu cada opção com a sua letra):
            {(draft.Doubts.Count == 0 ? "nenhuma" : JsonSerializer.Serialize(GoalExtraction.DoubtsForPrompt(draft.Doubts), Json))}

            Uma dúvida só é resolvida quando o usuário ESCOLHE uma opção: pela letra ou repetindo o conteúdo dela.
            "sim", "não", "ok", "pode ser" NÃO escolhem nenhuma opção: nesse caso mantenha a dúvida, sem mudar a meta.

            Classifique a resposta do usuário em decision:
            - "{Decisions.Confirm}": concorda com a proposta e quer salvar ("sim", "isso", "perfeito", "pode salvar").
            - "{Decisions.Skip}": quer deixar a meta como estava e seguir ("pular", "pula", "deixa assim", "depois eu vejo").
            - "{Decisions.Change}": escolhe uma opção de uma dúvida, ou corrige algo da proposta ("quero 5 livros",
              "o prazo é junho").
            - "{Decisions.Greeting}": é apenas um cumprimento ou conversa social ("oi", "olá", "bom dia").
            - "{Decisions.Other}": a mensagem não é sobre esta meta (uma pergunta ou pedido de outro assunto).

            goal: a meta COMPLETA e atualizada, aplicando o que o usuário disse e mantendo o resto da proposta.
            Para "{Decisions.Skip}", "{Decisions.Greeting}" e "{Decisions.Other}", devolva a proposta mostrada.
            assumptions: o que continua sendo suposição depois da resposta (tire o que o usuário confirmou ou corrigiu).
            doubts: as dúvidas que continuam sem escolha, copiadas como estão (sem as letras). Lista vazia se não sobrou.

            {GoalExtraction.GoalFieldRules}

            {GoalExtraction.DoubtRules}
            """;
    }

    private static class Decisions
    {
        public const string Confirm = "confirm";
        public const string Skip = "skip";
        public const string Change = "change";
        public const string Greeting = "greeting";
        public const string Other = "other";
    }

    internal sealed record DetailAnswer(
        string Decision,
        ExtractedGoal? Goal,
        List<string>? Assumptions,
        List<ExtractedDoubt>? Doubts);
}
