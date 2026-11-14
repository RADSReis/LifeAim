using LifeAim.Modules.Agents.Application.Workflow;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// ② Waiting for the user's name: the AI interprets the message, the code decides the edge.
/// One AI call returns both what the node needs (the name) and whether the message answered at all.
/// </summary>
internal sealed class AskNameNode(IChatClient chatClient, TimeProvider timeProvider) : INode
{
    // The node's whole instruction to the AI: one small task, so a small model is enough.
    private const string Instructions =
        """
        Você ajuda o Pippin, um assistente que conversa em português com o usuário para cadastrar metas de vida.
        O Pippin acabou de perguntar: "Como você se chama?".
        Sua única tarefa: decidir se a mensagem do usuário responde a essa pergunta e, se responder, extrair o nome.

        - answeredTheQuestion = true quando a mensagem diz como o usuário se chama ou quer ser chamado
          ("Rafael", "sou a Ana", "pode me chamar de Rafa").
        - name = o nome exatamente como o usuário quer ser chamado ("pode me chamar de Rafa" → "Rafa").
        - answeredTheQuestion = false, e name = null, para qualquer outra coisa: um cumprimento sem nome,
          uma pergunta, um pedido ou outro assunto.
        """;

    public const string Question = "Como você se chama?";

    public Stage Stage => Stage.AskName;

    public string? PendingQuestion(ConversationContext context) => Question;

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions),
            new(ChatRole.User, context.IncomingText),
        ];

        // Structured output: the model must answer with JSON matching NameAnswer, turned into the record.
        var response = await chatClient.GetResponseAsync<NameAnswer>(messages, cancellationToken: cancellationToken);

        if (!response.TryGetResult(out var answer)
            || !answer.AnsweredTheQuestion
            || string.IsNullOrWhiteSpace(answer.Name))
        {
            return NodeResult.NotExpected; // → the router
        }

        context.UserName = PersonName.Capitalize(answer.Name);

        var reply =
            $"Prazer, {context.UserName}! Vamos criar alguns objetivos e metas para você se tornar quem sempre sonhou ser? " +
            GoalHorizon.Question(timeProvider);

        return NodeResult.Answer(reply, nextStage: Stage.CollectGoals);
    }

    internal sealed record NameAnswer(bool AnsweredTheQuestion, string? Name);
}
