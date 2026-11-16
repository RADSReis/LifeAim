using System.Globalization;
using System.Text;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LifeAim.Modules.Agents.Application.Agent;

/// <summary>
/// Pippin's first real agent: conversations about the goals already saved (consultar, alterar, cancelar,
/// registrar progresso). Unlike the nodes, which make ONE AI call and let the code take the edge, here the AI
/// decides what to do: it gets the tools (<see cref="GoalTools"/>) and runs a loop until it has an answer:
///
///   1. the model reads the messages and the tool list, and answers "call record_progress(1, ...)";
///   2. FunctionInvokingChatClient (UseFunctionInvocation) runs that C# method and adds its result to the messages;
///   3. the model is called again with the result, and may call another tool or write the final reply.
///
/// The loop is capped (<see cref="MaxToolRounds"/>), every tool checks its arguments, and the destructive one only
/// asks: the cancellation itself waits for the user's "sim" in Stage.ConfirmCancelGoal.
/// </summary>
internal sealed class GoalsAgent
{
    /// <summary>How many times the model may call tools in one message before it must answer.</summary>
    public const int MaxToolRounds = 5;

    private const int HistoryInPrompt = 10;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly IChatClient _chatClient;
    private readonly IGoalService _goals;
    private readonly IProgressService _progress;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GoalsAgent> _logger;

    public GoalsAgent(
        IChatClient chatClient,
        IGoalService goals,
        IProgressService progress,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        // The same AI as the nodes, wrapped with the tool loop. Only this agent gets it: the nodes stay one call each.
        _chatClient = chatClient
            .AsBuilder()
            .UseFunctionInvocation(loggerFactory, loop => loop.MaximumIterationsPerRequest = MaxToolRounds)
            .Build();
        _goals = goals;
        _progress = progress;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<GoalsAgent>();
    }

    public async Task<AgentReply> ReplyAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var tools = new GoalTools(context, _goals, _progress, _timeProvider);

        // The recent conversation as real chat turns, so "e o prazo dela?" knows which goal "dela" is.
        List<ChatMessage> messages = [new(ChatRole.System, Instructions(context))];
        messages.AddRange(context.History.TakeLast(HistoryInPrompt).Select(turn =>
            new ChatMessage(turn.Role == ConversationRole.User ? ChatRole.User : ChatRole.Assistant, turn.Content)));
        messages.Add(new ChatMessage(ChatRole.User, context.IncomingText));

        var response = await _chatClient.GetResponseAsync(
            messages, new ChatOptions { Tools = tools.All() }, cancellationToken);

        _logger.LogInformation("Goals agent called [{Tools}]", string.Join(", ", tools.Called));
        return new AgentReply(response.Text.Trim(), tools.CancellationRequested);
    }

    private string Instructions(ConversationContext context)
    {
        var today = GoalHorizon.Today(_timeProvider);
        var goals = new StringBuilder();
        for (var i = 0; i < context.SavedGoals.Count; i++)
        {
            var goal = context.SavedGoals[i];
            var values = GoalExtraction.Values(GoalExtraction.FromSaved(goal));
            goals.Append(i + 1).Append(". ").Append(goal.Name)
                .Append(values is null ? "" : $" — {values}")
                .Append(goal.DueDate is { } due ? $" · até {due:yyyy-MM-dd}" : "")
                .Append('\n');
        }

        return $"""
            Você é o Pippin, o assistente do LifeAim: conversa em português, de forma leve, calorosa e natural, pelo
            Discord, sobre as metas de vida do usuário{(context.UserName is { } name ? $" ({name})" : "")}.
            Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)} ({today:yyyy-MM-dd}).

            Metas salvas do usuário (use o número nas ferramentas):
            {goals.ToString().TrimEnd()}

            Nesta conversa você pode, usando as ferramentas:
            - Consultar uma meta: chame get_goal e mostre as informações completas de forma organizada e curta.
              Para "quais são minhas metas?", a lista acima basta.
            - Alterar uma meta (nome, quantidade, prazo, valores): chame update_goal com só o que muda. Se a mudança
              deixar o plano incoerente (ex.: dobrou a quantidade), diga isso e ofereça ajustar o plano.
            - Alterar o plano (dias, horários, quanto por vez): chame replace_goal_plan com TODAS as rotinas que devem
              ficar (veja as atuais com get_goal antes). Salve só o que o usuário disse ou aprovou; se ele pedir uma
              sugestão, proponha e pergunte se pode salvar (depois do sim, salve com proposedByPippin true).
            - Cancelar ou excluir uma meta: chame request_goal_cancellation. Nunca diga que a meta foi cancelada: o
              sistema pede a confirmação.
            - Registrar progresso ("hoje li 10 páginas", "corri 5 km ontem", "pesei 92 kg"): chame record_progress,
              mesmo que a unidade não seja a do indicador da meta. "ontem", "sábado" → calcule a data a partir de hoje.
              Depois, comemore de forma curta e use os totais que a ferramenta devolveu (ex.: "já são 40 páginas
              nesta semana!"). Valorize a consistência: se fez muito mais que o plano de uma vez, elogie e lembre, com
              leveza, que distribuir ao longo dos dias costuma funcionar melhor.

            Regras:
            - Use as ferramentas para tudo o que ler ou mudar dados. Nunca diga que salvou, alterou ou registrou algo
              sem a ferramenta ter confirmado. Se ela devolver "Erro", explique ao usuário em palavras simples.
            - Se não der para saber de qual meta o usuário fala, pergunte com opções com letras (A, B, C) com os nomes
              das metas. Nunca pergunte sim/não para isso.
            - Se faltar algo para fazer uma alteração, pergunte só o essencial, numa frase.
            - Não invente números, datas ou informações que o usuário não disse e as ferramentas não mostraram.
            - Fale só das metas do usuário: não converse sobre outros assuntos.
            - Respostas curtas, no estilo de chat. Use **negrito** no nome das metas. Sem tabelas e sem títulos (#).
            - Não mostre números internos, ids ou nomes de ferramentas. Não pergunte se ele quer adicionar metas novas.
            """;
    }
}

/// <summary>What the agent answered, and the goal it asked to cancel (if any), which the workflow confirms.</summary>
internal sealed record AgentReply(string Text, GoalDto? CancellationRequested);
