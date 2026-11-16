using System.Globalization;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// The pause of a goal ended (Funcionalidade 6) and Pippin asked whether it comes back. ONE AI call reads the answer;
/// the code does the rest: resume (the plan as it was), keep it paused (with a new day, if the user said one), or, when
/// the user does not want it any more, the same explicit confirmation as any cancellation. Never pushes the user to go
/// on with a goal that no longer makes sense.
/// </summary>
internal sealed class ResumeGoalNode(IChatClient chatClient, IGoalService goals, TimeProvider timeProvider) : INode
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public Stage Stage => Stage.ResumeGoal;

    public static string Question(string goalName) =>
        $"Quer retomar **{goalName}** agora? Responda **sim** para voltar com o plano que ela tinha, ou me diga se prefere " +
        "mais um tempo (ou se ela já não faz sentido).";

    public string? PendingQuestion(ConversationContext context) => Goal(context) is { } goal ? Question(goal.Name) : null;

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var draft = context.Draft ?? GoalsDraft.Empty(null);
        if (Goal(context) is not { } goal)
        {
            context.Draft = draft with { ResumeGoalId = null };
            return NodeResult.NotExpectedAfter(null, Stage.CollectGoals);
        }

        var today = GoalHorizon.Today(timeProvider);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(goal.Name, today)),
            new(ChatRole.User, context.IncomingText),
        ];
        var response = await chatClient.GetResponseAsync<ResumeAnswer>(messages, cancellationToken: cancellationToken);
        var decision = response.TryGetResult(out var answer) ? answer.Decision?.Trim().ToLowerInvariant() : null;
        var back = draft with { ResumeGoalId = null };

        switch (decision)
        {
            case "resume":
                context.Draft = back;
                return await goals.ResumeAsync(goal.Id, context.UserId, cancellationToken)
                    ? NodeResult.Answer(
                        $"Que bom ter **{goal.Name}** de volta! 🌱 O plano continua o mesmo; se quiser ajustar alguma coisa, é só me falar.",
                        Stage.CollectGoals)
                    : NodeResult.Answer("Essa meta já não está pausada.", Stage.CollectGoals);

            case "more_time":
            {
                context.Draft = back;
                DateOnly? day = GoalExtraction.ParseDate(answer!.ResumeOn) is { } parsed && parsed > today ? parsed : null;
                await goals.PauseAsync(goal.Id, context.UserId, day, null, cancellationToken);
                var until = day is { } d
                    ? $"até {d.ToString("dd/MM", PtBr)}; nesse dia eu pergunto de novo"
                    : "sem data; quando quiser voltar, é só me falar";
                return NodeResult.Answer($"Tudo bem, **{goal.Name}** continua pausada {until}. 🙂", Stage.CollectGoals);
            }

            case "end":
                // Ending is destructive: the explicit "sim" of the cancellation, asked by the code.
                context.Draft = back with
                {
                    CancelGoalId = goal.Id,
                    ReturnStage = Stage.CollectGoals,
                    CancelReason = string.IsNullOrWhiteSpace(answer!.Reason) ? null : answer.Reason.Trim(),
                    Replacement = null,
                };
                return NodeResult.Answer(ConfirmCancelGoalNode.Question(goal.Name), Stage.ConfirmCancelGoal);

            case "greeting":
                return NodeResult.Greeting;

            default:
                // Not an answer: the question lapses (the goal stays paused) and the message goes on.
                context.Draft = back;
                return NodeResult.NotExpectedAfter(null, Stage.CollectGoals);
        }
    }

    private static GoalDto? Goal(ConversationContext context) =>
        context.Draft?.ResumeGoalId is { } id ? context.PausedGoals.FirstOrDefault(g => g.Id == id) : null;

    private static string Instructions(string goalName, DateOnly today) =>
        $"""
         Você ajuda o Pippin, um assistente que conversa em português sobre as metas de vida do usuário. Hoje é
         {today.ToString("dddd, dd/MM/yyyy", PtBr)} ({today:yyyy-MM-dd}). A pausa da meta "{goalName}" terminou e o Pippin
         perguntou se ele quer retomá-la. Classifique a resposta em decision:
         - "resume": quer retomar ("sim", "bora", "quero voltar").
         - "more_time": quer mais um tempo de pausa ("ainda não", "mês que vem", "daqui a duas semanas"). resumeOn: o dia
           da volta em yyyy-MM-dd, se ele disse ou dá para calcular; senão null.
         - "end": não quer mais essa meta ("desisti", "não faz mais sentido", "pode tirar"). reason: o motivo nas
           palavras dele, se disse.
         - "greeting": é só um cumprimento.
         - "other": qualquer outra coisa (outra meta, um progresso, uma pergunta).
         """;

    internal sealed record ResumeAnswer(string? Decision, string? ResumeOn, string? Reason);
}
