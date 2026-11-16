using System.Text;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// When the node of the current stage did not understand the message, one AI call reads the context (stage,
/// pending question, recent messages, saved goals) and picks the best way on: another stage, a clarification
/// of the current question, a conversation about the saved goals (the goals agent), or "out of scope". It only
/// chooses; the workflow checks the choice against <see cref="AllowedPaths"/> and applies it. A classifier, not an
/// agent: one call, no tools, no loop. The agent with tools is the "goal_talk" route.
/// </summary>
internal sealed class PathClassifier(IChatClient chatClient)
{
    /// <summary>
    /// Where the conversation may jump from each stage, and what each jump is for (the AI reads the text).
    /// Anything else is refused by code: e.g. ConfirmGoals → DetailGoals would detail goals not saved yet.
    /// </summary>
    public static readonly IReadOnlyDictionary<Stage, IReadOnlyDictionary<Stage, string>> AllowedPaths =
        new Dictionary<Stage, IReadOnlyDictionary<Stage, string>>
        {
            [Stage.CollectGoals] = new Dictionary<Stage, string>
            {
                [Stage.DetailGoals] =
                    "aprofundar uma meta JÁ SALVA respondendo às perguntas do Pippin para montar o plano dela " +
                    "(\"vamos detalhar a meta de leitura\") (informe goalNumber)",
                [Stage.ChoosePlanGoal] =
                    "ver a PROPOSTA de planejamento que o Pippin preparou e enviou (\"quero ver a proposta\", " +
                    "\"vamos continuar a proposta\"); não serve para detalhar ou aprofundar uma meta",
            },
            [Stage.OfferPlan] = new Dictionary<Stage, string>
            {
                [Stage.CollectGoals] = "cadastrar uma meta NOVA, que ainda não existe",
            },
            [Stage.ChoosePlanGoal] = new Dictionary<Stage, string>
            {
                [Stage.CollectGoals] = "cadastrar uma meta NOVA, que ainda não existe",
            },
            [Stage.RefinePlanGoal] = new Dictionary<Stage, string>
            {
                [Stage.CollectGoals] = "cadastrar uma meta NOVA, que ainda não existe",
            },
            [Stage.DetailGoals] = new Dictionary<Stage, string>
            {
                [Stage.CollectGoals] = "cadastrar uma meta NOVA, que ainda não existe",
                [Stage.DetailGoals] = "detalhar OUTRA meta salva, em vez da que está em foco (informe goalNumber)",
            },
        };

    private const int HistoryInPrompt = 10;
    private const int MaxMessageLength = 300;

    public async Task<PathDecision> ClassifyAsync(
        ConversationContext context,
        string? pendingQuestion,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(context, pendingQuestion)),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<PathAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return PathDecision.OutOfScope("the AI answer could not be read");
        }

        var reason = answer.Reason ?? "";

        // The model sometimes writes the stage itself as the route ("route": "DetailGoals"): that is a goto.
        if (Enum.TryParse<Stage>(answer.Route?.Trim(), ignoreCase: true, out var routeStage))
        {
            answer = answer with { Route = "goto", Stage = answer.Stage ?? routeStage.ToString() };
        }

        switch (answer.Route?.Trim().ToLowerInvariant())
        {
            case "goto" when Enum.TryParse<Stage>(answer.Stage, ignoreCase: true, out var stage):
                var goal = answer.GoalNumber is { } n && n >= 1 && n <= context.SavedGoals.Count
                    ? context.SavedGoals[n - 1].Id
                    : (Guid?)null;
                return new PathDecision(PathKind.Goto, stage, goal, null, reason);

            case "clarify":
                return new PathDecision(PathKind.Clarify, null, null, Blank(answer.Clarification), reason);

            case "goal_talk" when context.SavedGoals.Count > 0:
                return new PathDecision(PathKind.GoalTalk, null, null, null, reason);

            default:
                return PathDecision.OutOfScope(reason);
        }
    }

    /// <summary>True when the code allows the jump the AI chose.</summary>
    public static bool IsAllowed(Stage from, Stage to) =>
        AllowedPaths.TryGetValue(from, out var paths) && paths.ContainsKey(to);

    private static string Instructions(ConversationContext context, string? pendingQuestion)
    {
        var paths = AllowedPaths.GetValueOrDefault(context.Stage);
        var pathList = paths is { Count: > 0 }
            ? string.Join("\n", paths.Select(p => $"- {p.Key}: {p.Value}"))
            : "(nenhuma: nesta etapa só existem clarify e out_of_scope)";

        var goals = context.SavedGoals.Count == 0
            ? "nenhuma"
            : string.Join("\n", context.SavedGoals.Select((g, i) => $"{i + 1}. {g.Name}"));

        // Only with saved goals: there is nothing to consult, change, cancel or report progress on before that.
        var goalTalk = context.SavedGoals.Count == 0
            ? ""
            : """
              - "goal_talk": a mensagem é sobre as metas JÁ SALVAS: ver ou consultar informações ("me mostra a meta do
                peso", "quais são minhas metas?", "como estou na leitura?"), alterar (nome, quantidade, prazo, valores,
                dias ou horários do plano), cancelar ou excluir uma meta, ou registrar progresso ("hoje li 10 páginas",
                "corri 5 km ontem", "pesei 92 kg"). Prefira goal_talk a goto para esses pedidos.
              """;

        return $"""
            Você ajuda o Pippin, um assistente que conversa em português para cadastrar e acompanhar as metas de vida
            do usuário. A etapa atual da conversa não entendeu a última mensagem. Decida o melhor caminho a seguir.

            Etapa atual: {context.Stage}
            Pergunta que o Pippin está esperando ser respondida: "{pendingQuestion ?? "nenhuma"}"
            Metas já salvas do usuário:
            {goals}

            Conversa recente (da mais antiga para a mais nova):
            {History(context)}

            Caminhos possíveis (route):
            - "goto": a mensagem pertence a outra etapa. Use só uma destas etapas (stage), exatamente com este nome:
            {pathList}
            - "clarify": a mensagem tenta responder a pergunta da etapa atual, mas ficou confusa ou incompleta.
              Escreva em clarification UMA frase curta, em português, dizendo o que o Pippin precisa (sem repetir a pergunta).
            {goalTalk}
            - "out_of_scope": um pedido ou assunto sem relação com as metas do usuário (piada, previsão do tempo,
              receita, perguntas gerais).

            goalNumber: o número da meta salva da lista acima, quando o caminho pede uma meta; senão null.
            reason: uma frase curta explicando a escolha (vai para o log, o usuário não vê).
            Na dúvida entre "goto" e "clarify", prefira "clarify": é melhor perguntar do que levar o usuário para o lugar errado.
            """;
    }

    private static string History(ConversationContext context)
    {
        var text = new StringBuilder();
        foreach (var turn in context.History.TakeLast(HistoryInPrompt))
        {
            var content = turn.Content.Length > MaxMessageLength ? turn.Content[..MaxMessageLength] + "…" : turn.Content;
            text.Append(turn.Role == ConversationRole.User ? "Usuário: " : "Pippin: ")
                .Append(content.Replace('\n', ' '))
                .Append('\n');
        }

        return text.Length == 0 ? "(sem mensagens anteriores)" : text.ToString().TrimEnd();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record PathAnswer(string? Route, string? Stage, int? GoalNumber, string? Clarification, string? Reason);
}

internal enum PathKind
{
    Goto,
    Clarify,

    /// <summary>A conversation about the saved goals: the goals agent (with tools) answers it.</summary>
    GoalTalk,

    OutOfScope,
}

/// <param name="Stage">For <see cref="PathKind.Goto"/>: where to go.</param>
/// <param name="GoalId">For a stage that works on one saved goal (DetailGoals).</param>
/// <param name="Clarification">For <see cref="PathKind.Clarify"/>: what Pippin needs, in one sentence.</param>
/// <param name="Reason">Why the AI chose this, for the log.</param>
internal sealed record PathDecision(PathKind Kind, Stage? Stage, Guid? GoalId, string? Clarification, string Reason)
{
    public static PathDecision OutOfScope(string reason) => new(PathKind.OutOfScope, null, null, null, reason);

    public static PathDecision Clarify(string reason) => new(PathKind.Clarify, null, null, null, reason);
}
