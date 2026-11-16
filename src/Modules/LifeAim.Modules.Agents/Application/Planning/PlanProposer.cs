using System.Globalization;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Planning;

/// <summary>
/// Writes the first plan proposal of a user's goals, in the background (Funcionalidade 2, "Preparação em segundo
/// plano"): ONE AI call reads every goal at once, with the plan the user already described, so it can weigh the
/// effort of each one and the conflicts between them (two activities in the same evening, too much in one week).
/// For each goal it returns a concrete suggestion with its reasoning (proposal before question), the routines and
/// one contextual question. The code checks the answer; nothing is shown to the user here.
/// </summary>
internal sealed class PlanProposer(IChatClient chatClient, TimeProvider timeProvider)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The proposal for each goal the AI answered about, in the order it suggests talking about them.</summary>
    public async Task<IReadOnlyList<GoalProposalInput>> ProposeAsync(
        IReadOnlyList<GoalDto> goals,
        IReadOnlyList<RoutineDto> routines,
        CancellationToken cancellationToken)
    {
        if (goals.Count == 0)
        {
            return [];
        }

        List<ChatMessage> messages = [new(ChatRole.System, Instructions(goals, routines))];
        var response = await chatClient.GetResponseAsync<ProposalAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            throw new InvalidOperationException("The AI answer for the plan proposal could not be read.");
        }

        return ToProposals(answer, goals);
    }

    /// <summary>Only goals that exist, once each, with a message and a question; routines checked by GoalPlan.</summary>
    internal static IReadOnlyList<GoalProposalInput> ToProposals(ProposalAnswer answer, IReadOnlyList<GoalDto> goals) =>
        (answer.Goals ?? [])
            .Where(g => g.GoalNumber is { } n && n >= 1 && n <= goals.Count)
            .Where(g => !string.IsNullOrWhiteSpace(g.Message) && !string.IsNullOrWhiteSpace(g.Question))
            .DistinctBy(g => g.GoalNumber)
            .Select(g => new GoalProposalInput(
                goals[g.GoalNumber!.Value - 1].Id,
                g.Message!.Trim(),
                g.Question!.Trim(),
                GoalPlan.ToRoutines(g.Routines, RoutineSource.Pippin)))
            .ToList();

    private string Instructions(IReadOnlyList<GoalDto> goals, IReadOnlyList<RoutineDto> routines)
    {
        var today = GoalHorizon.Today(timeProvider);
        var numbered = goals.Select((goal, i) => new
        {
            goalNumber = i + 1,
            goal.Name,
            goal.Description,
            goal.Indicator,
            goal.StartValue,
            goal.TargetValue,
            goal.Unit,
            dueDate = goal.DueDate?.ToString("yyyy-MM-dd"),
            goal.UserPlan,
            currentRoutines = routines.Where(r => r.GoalId == goal.Id).Select(r => GoalPlan.Describe(ToInput(r))).ToList(),
        });

        return $"""
            Você é o Pippin, um assistente que conversa em português, de forma leve e natural, e ajuda o usuário a
            transformar metas de vida em ações. Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.

            O usuário acabou de cadastrar estas metas (JSON). userPlan é o que ele disse, com as palavras dele, e
            currentRoutines é o plano já combinado (pode estar vazio):
            {JsonSerializer.Serialize(numbered, Json)}

            Prepare a primeira proposta de planejamento, olhando TODAS as metas juntas:
            - Estime o esforço de cada meta até o prazo (ex.: 2 livros de 200 páginas até 31/12 → ~10 páginas por dia).
            - Respeite o que o usuário já disse e o plano já combinado: ajuste só o necessário para caber no prazo.
            - Evite conflitos: atividades no mesmo período do mesmo dia, ou esforço demais na mesma semana.
            - Prefira pouco e todo dia (ou em dias fixos) a muito de uma vez: consistência é o melhor caminho.
            - Nada de horários exatos que o usuário não disse; use períodos do dia ("de manhã", "à noite").

            goals: uma entrada por meta, na ordem em que vale a pena conversar sobre elas (as que mais precisam de um
            plano primeiro). Cada uma tem:
            - goalNumber: o número da meta acima.
            - message: a proposta concreta para esta meta, em até 60 palavras, dirigida ao usuário, começando pela
              sugestão e explicando o raciocínio em uma frase (ex.: "Para concluir 2 livros até o fim do ano, podemos
              ler 10 páginas por dia. Nesse ritmo você termina cada livro em uns 20 dias e ainda sobra folga."). Use
              **negrito** no nome da meta. Não termine com pergunta.
            - question: UMA pergunta contextual, curta (até 30 palavras), para adaptar a proposta ao padrão real do
              usuário (ex.: "Como costuma ser sua leitura? Prefere ler um pouco todo dia ou mais nos fins de semana?").
            - routines: a proposta quebrada em rotinas.
            {GoalPlan.RoutineRules}
            """;
    }

    private static RoutineInput ToInput(RoutineDto r) =>
        new(r.Activity, r.Frequency, r.Source, r.Interval, r.DaysOfWeek, r.TimesPerPeriod, r.TimeOfDay, r.DayPeriod,
            r.Amount, r.Unit, r.DurationMinutes, r.StartsOn, r.EndsOn);

    internal sealed record ProposalAnswer(List<ProposedGoal>? Goals);

    internal sealed record ProposedGoal(int? GoalNumber, string? Message, string? Question, List<ExtractedRoutine>? Routines);
}
