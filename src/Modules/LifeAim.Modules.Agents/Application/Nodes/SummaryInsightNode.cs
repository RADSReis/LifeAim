using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// The monthly summary ended with "Tive um insight sobre seu hábito de leitura. Quer saber qual?" (Funcionalidade 4,
/// "Convite opcional"). Yes → the insight; no → the conversation goes on, without insisting. Anything else drops the
/// invitation and is handled as if it had not been made. Afterwards, a follow-up still open is resumed.
/// </summary>
internal sealed class SummaryInsightNode(IChatClient chatClient, IReportingService reporting) : INode
{
    public const string Declined = "Tudo bem! 🙂";

    public Stage Stage => Stage.SummaryInsight;

    public string? PendingQuestion(ConversationContext context) => "Quer saber qual é o insight?";

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var draft = context.Draft ?? GoalsDraft.Empty(null);
        var after = draft.CheckIns is { Count: > 0 } ? Stage.CheckIn : Stage.CollectGoals;

        if (draft.SummaryId is not { } summaryId
            || await reporting.FindAsync(summaryId, context.UserId, cancellationToken) is not { InsightStatus: InsightStatus.Offered } summary)
        {
            context.Draft = draft with { SummaryId = null };
            return NodeResult.NotExpectedAfter(null, after);
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(summary.InsightTeaser!)),
            new(ChatRole.User, context.IncomingText),
        ];
        var response = await chatClient.GetResponseAsync<InsightAnswer>(messages, cancellationToken: cancellationToken);
        var decision = response.TryGetResult(out var answer) ? answer.Decision?.Trim().ToLowerInvariant() : null;

        switch (decision)
        {
            case "yes":
                await reporting.AnswerInsightAsync(summaryId, context.UserId, accepted: true, cancellationToken);
                context.Draft = draft with { SummaryId = null };
                return Finish(summary.Insight!, after);

            case "no":
                await reporting.AnswerInsightAsync(summaryId, context.UserId, accepted: false, cancellationToken);
                context.Draft = draft with { SummaryId = null };
                return Finish(Declined, after);

            case "greeting":
                return NodeResult.Greeting;

            default:
                // Not an answer to the invitation: it lapses (still "offered" in the database) and the message goes on.
                context.Draft = draft with { SummaryId = null };
                return NodeResult.NotExpectedAfter(null, after);
        }
    }

    // Back to an unanswered follow-up: its question again. Otherwise just between tasks.
    private static NodeResult Finish(string reply, Stage after) =>
        after == Stage.CheckIn ? NodeResult.Resume(reply, after) : NodeResult.Answer(reply, after);

    private static string Instructions(string teaser) =>
        $"""
         Você ajuda o Pippin, um assistente que conversa em português. Depois do resumo do mês, o Pippin perguntou:
         "{teaser}"

         Classifique a resposta do usuário em decision:
         - "yes": quer saber ("sim", "quero", "conta", "manda", "qual?").
         - "no": não quer agora ("não", "agora não", "depois", "deixa").
         - "greeting": é apenas um cumprimento ou conversa social ("oi", "bom dia").
         - "other": a mensagem é sobre outro assunto (uma meta, um progresso, uma pergunta, um comentário sobre o resumo).
         """;

    internal sealed record InsightAnswer(string? Decision);
}
