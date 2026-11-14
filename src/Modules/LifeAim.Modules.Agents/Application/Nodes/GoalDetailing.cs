using System.Globalization;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// Detailing saved goals one at a time (UX "Cadastro e aprofundamento das metas"), shared by the node that
/// saves them (ConfirmGoals, to start) and the node that details them (DetailGoals, to go on):
/// the next goal in the queue gets a proposal from the AI; with none left, the general summary.
/// </summary>
internal sealed class GoalDetailing(IChatClient chatClient, IGoalService goals, TimeProvider timeProvider)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The saved goals the conversation should still detail: the ones that are not plannable yet.</summary>
    public static List<Guid> NotPlannable(IEnumerable<GoalDto> saved) =>
        saved.Where(g => !GoalExtraction.IsPlannable(g.Indicator, g.TargetValue, g.DueDate)).Select(g => g.Id).ToList();

    /// <summary>
    /// Proposes the first goal of <paramref name="queue"/> (→ DetailGoals) or, when none is left, shows the
    /// summary of all goals (→ CollectGoals). <paramref name="prefix"/> opens the reply ("Salvei! ✅").
    /// </summary>
    public async Task<NodeResult> NextAsync(
        ConversationContext context,
        IEnumerable<Guid> queue,
        string prefix,
        CancellationToken cancellationToken)
    {
        var horizon = context.Draft?.Horizon;
        var saved = await goals.ListAsync(context.UserId, cancellationToken);
        var remaining = queue.Where(id => saved.Any(g => g.Id == id)).ToList(); // a deleted goal just drops out

        if (remaining.Count == 0)
        {
            context.Draft = GoalsDraft.Empty(horizon);
            var more = horizon is { } date
                ? $"Quer adicionar mais alguma meta até {date.ToString("dd/MM/yyyy", PtBr)}?"
                : "Quer adicionar mais alguma meta?";
            return NodeResult.Answer(
                $"{prefix}\n\nPronto, terminamos de detalhar! 🎉\n\n**Resumo das suas metas:**\n" +
                $"{GoalExtraction.List(saved.Select(GoalExtraction.FromSaved))}\n\n{more}",
                nextStage: Stage.CollectGoals);
        }

        var goal = saved.First(g => g.Id == remaining[0]);
        context.Draft = await ProposeAsync(goal, remaining, horizon, cancellationToken);
        return NodeResult.Answer($"{prefix}\n\n{GoalExtraction.Proposal(context.Draft)}", nextStage: Stage.DetailGoals);
    }

    // One AI call: complete what the goal misses, with reasonable assumptions the user will check.
    private async Task<GoalsDraft> ProposeAsync(
        GoalDto goal,
        IReadOnlyList<Guid> queue,
        DateOnly? horizon,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> messages = [new(ChatRole.System, Instructions(goal, horizon))];
        var response = await chatClient.GetResponseAsync<ProposalAnswer>(messages, cancellationToken: cancellationToken);

        var asSaved = GoalExtraction.FromSaved(goal);
        if (!response.TryGetResult(out var answer))
        {
            return new GoalsDraft(horizon, [asSaved], [], queue); // shows the goal as it is; the user can still adjust
        }

        var proposal = GoalExtraction.ToDrafts(answer.Goal is null ? [] : [answer.Goal], horizon).FirstOrDefault() ?? asSaved;
        return new GoalsDraft(
            horizon,
            [proposal],
            GoalExtraction.ToDoubts(answer.Doubts),
            queue,
            GoalExtraction.CleanAssumptions(answer.Assumptions));
    }

    private string Instructions(GoalDto goal, DateOnly? horizon)
    {
        var today = GoalHorizon.Today(timeProvider);
        var saved = new ExtractedGoal(
            goal.Name, goal.Description, goal.Indicator, goal.StartValue, goal.TargetValue, goal.Unit,
            goal.DueDate?.ToString("yyyy-MM-dd"));

        return $"""
            Você ajuda o Pippin, um assistente que conversa em português para cadastrar as metas de vida do usuário.
            Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.{(horizon is { } h ? $" O período combinado vai até {h:yyyy-MM-dd}." : "")}

            O usuário já salvou esta meta (JSON):
            {JsonSerializer.Serialize(saved, Json)}

            Sua tarefa: completar o que falta para ela ficar planejável: indicator (o que se mede), targetValue (quanto)
            e dueDate (até quando); unit e startValue quando fizerem sentido. O usuário vai conferir a proposta.
            - Mantenha o que já está preenchido; mude só o que estiver claramente incoerente.
            - Para o que falta, proponha uma suposição razoável a partir do nome, da descrição e do período. Aqui você
              PODE propor valores que o usuário não disse. Ex.: "Ler mais" → indicator "livros terminados",
              targetValue 2, unit "livros"; sem prazo → o fim do período.
            - Uma meta que não parece mensurável ("ser mais feliz") ganha um indicador concreto e simples
              ("dias da semana em que me senti bem").
            - goal.name: o mesmo nome; ajuste só para refletir o número proposto ("Ler mais" → "Ler 2 livros").
            - assumptions: no máximo 2 frases curtas (até 8 palavras), dirigidas ao usuário, dizendo por que você
              escolheu o que não estava na meta. Não repita os campos que já aparecem na proposta ("indicador: ...")
              e não comente o nome nem a unidade. Ex.: ["3 livros é um bom ritmo até dezembro", "prazo no fim do período"].
              Lista vazia se não supôs nada.
            - doubts: só quando uma suposição for arriscada e mudar muito a meta.

            {GoalExtraction.GoalFieldRules}

            {GoalExtraction.DoubtRules}
            """;
    }

    internal sealed record ProposalAnswer(ExtractedGoal? Goal, List<string>? Assumptions, List<ExtractedDoubt>? Doubts);
}
