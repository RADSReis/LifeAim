using System.Globalization;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// Deepening the saved goals one at a time (UX "Cadastro e aprofundamento das metas"), shared by the node that
/// saves them (ConfirmGoals, to start) and the node that deepens them (DetailGoals, to go on): the next goal in
/// the queue gets ONE contextual question from the AI (what is missing + the plan); with none left, the summary.
/// </summary>
internal sealed class GoalDetailing(
    IChatClient chatClient,
    IGoalService goals,
    IPlanningService planning,
    TimeProvider timeProvider)
{
    /// <summary>The end of the intake (Funcionalidade 2, "Ao finalizar o cadastro"), with the Notion page's words.</summary>
    public const string PreparingProposal =
        "Já entendi melhor o que você quer conquistar. Agora vou analisar suas metas e considerar o tempo que você " +
        "tem disponível para preparar uma primeira sugestão de planejamento. Não precisa se preocupar com essa parte " +
        "agora — vou organizar uma proposta e depois conversamos sobre ela. 😉";

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Asks about the first goal of <paramref name="queue"/> (→ DetailGoals) or, when none is left, shows the
    /// summary of all goals (→ CollectGoals). <paramref name="prefix"/> opens the reply ("Salvei! ✅").
    /// Without <paramref name="withQuestion"/> the reply leaves out the question that ends it (the next goal's
    /// question, or "quer adicionar mais?"), for when another part of the workflow will ask its own.
    /// </summary>
    public async Task<NodeResult> NextAsync(
        ConversationContext context,
        IEnumerable<Guid> queue,
        string prefix,
        CancellationToken cancellationToken,
        bool withQuestion = true)
    {
        var horizon = context.Draft?.Horizon;
        var proposeAfter = context.Draft?.ProposeAfter ?? false;
        var saved = await goals.ListAsync(context.UserId, cancellationToken);
        var remaining = queue.Where(id => saved.Any(g => g.Id == id)).ToList(); // a deleted goal just drops out

        if (remaining.Count == 0)
        {
            context.Draft = GoalsDraft.Empty(horizon);
            var summary = $"{prefix}\n\nPronto, terminamos! 🎉\n\n{GoalPlan.Summary(saved)}";

            // Right after the intake: the plan proposal is prepared in the background, nothing more is asked now.
            if (proposeAfter && saved.Count > 0)
            {
                await planning.RequestAsync(context.UserId, cancellationToken);
                return NodeResult.Answer($"{summary}\n\n{PreparingProposal}", nextStage: Stage.CollectGoals);
            }

            var more = horizon is { } date
                ? $"Quer adicionar mais alguma meta até {date.ToString("dd/MM/yyyy", PtBr)}?"
                : "Quer adicionar mais alguma meta?";
            return NodeResult.Answer(withQuestion ? $"{summary}\n\n{more}" : summary, nextStage: Stage.CollectGoals);
        }

        var goal = GoalExtraction.FromSaved(saved.First(g => g.Id == remaining[0]));
        var question = await AskAsync(goal, horizon, cancellationToken);
        context.Draft = new GoalsDraft(horizon, [goal], [], remaining, Question: question, ProposeAfter: proposeAfter);
        return NodeResult.Answer(withQuestion ? $"{prefix}\n\n{question}" : prefix, nextStage: Stage.DetailGoals);
    }

    /// <summary>The goal with what the user added, and its plan, written to the Goals module.</summary>
    public async Task SaveAsync(Guid userId, Guid goalId, GoalDraft goal, PlanDraft? plan, CancellationToken cancellationToken)
    {
        await goals.UpdateAsync(
            new UpdateGoalRequest(
                goalId, userId, goal.Name, goal.Description, goal.Indicator, goal.DueDate,
                goal.StartValue, goal.TargetValue, goal.Unit, goal.UserPlan),
            cancellationToken);

        if (plan is { Routines.Count: > 0 })
        {
            var routines = GoalPlan.WithDates(plan.Routines, goal.DueDate, GoalHorizon.Today(timeProvider));
            await goals.ReplaceRoutinesAsync(goalId, userId, routines, cancellationToken);
        }
    }

    /// <summary>The fallback when the AI fails: still one question, still offering a proposal.</summary>
    public static string DefaultQuestion(GoalDraft goal) =>
        $"Para conseguir **{goal.Name}**, qual o seu plano? Me diga em poucas palavras, ou se quiser eu posso propor um plano para você.";

    // One AI call writes the one question for this goal (Funcionalidade 1, decision 2: one broad, smart question).
    private async Task<string> AskAsync(GoalDraft goal, DateOnly? horizon, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages = [new(ChatRole.System, Instructions(goal, horizon))];
        var response = await chatClient.GetResponseAsync<QuestionAnswer>(messages, cancellationToken: cancellationToken);

        return response.TryGetResult(out var answer) && !string.IsNullOrWhiteSpace(answer.Question)
            ? answer.Question.Trim()
            : DefaultQuestion(goal);
    }

    private string Instructions(GoalDraft goal, DateOnly? horizon)
    {
        var today = GoalHorizon.Today(timeProvider);
        return $"""
            Você ajuda o Pippin, um assistente que conversa em português, de forma leve e natural, para cadastrar as
            metas de vida do usuário. Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.{(horizon is { } h ? $" O período combinado vai até {h:yyyy-MM-dd}." : "")}

            O usuário acabou de salvar esta meta (JSON):
            {JsonSerializer.Serialize(GoalExtraction.ToExtracted(goal), Json)}

            Escreva UMA única mensagem curta para aprofundar esta meta: uma pergunta de no máximo 25 palavras, mais a
            frase final abaixo. Ela será a única pergunta sobre esta meta, então junte nela o essencial que falta,
            em linguagem de conversa, sem listar possibilidades nem pedir detalhes demais (não é um formulário):
            - O valor de HOJE, quando a meta mede um estado que já existe (peso, dinheiro guardado, km que já corre)
              e startValue é null. Ex.: "quanto você pesa hoje?". Metas de quantidade a fazer no período ("ler 2 livros")
              começam do zero: não pergunte valor de hoje.
            - O que muda o esforço, quando fizer sentido para esse tipo de meta (o tamanho dos livros, por exemplo).
            - O plano: como ele pretende chegar lá (dias, horários, quanto por vez). Se userPlan já diz isso, pergunte
              só o que falta no plano.
            - Termine oferecendo ajuda, com estas palavras: "Me diga em poucas palavras, ou se quiser eu posso propor
              um plano para você."
            Não repita o que já está na meta. Não use listas. Use **negrito** no nome da meta.
            Ex.: "Para chegar a **90 kg** até o Natal, quanto você pesa hoje e qual o seu plano? Me diga em poucas
            palavras, ou se quiser eu posso propor um plano para você."
            """;
    }

    internal sealed record QuestionAnswer(string? Question);
}
