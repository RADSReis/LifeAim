using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Balance;

/// <summary>
/// Writes the message of a priorities review (Funcionalidade 5). The code already decided everything (which goals are
/// working, which one is in focus, the signals, the absolute progress and the projection): ONE AI call only turns those
/// facts into a warm message. The question at the end is written by the code, so it is always an open one that
/// investigates without presuming the cause. If the AI answer cannot be read, a plain message goes instead.
/// </summary>
internal sealed class BalanceWriter(IChatClient chatClient)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The open question that ends every review; also the stage's pending question.</summary>
    public static string Question(string goalName) =>
        $"O que você sente que está tornando **{goalName}** mais difícil de encaixar? Me conta, sem pressa: " +
        "a ideia é pensarmos juntos num ajuste pequeno, sem mexer no que está funcionando.";

    public async Task<string> WriteAsync(
        BalanceDto balance,
        IReadOnlyList<RoutineDto> routines,
        string? userName,
        CancellationToken cancellationToken)
    {
        var focus = balance.Focus ?? throw new ArgumentException("A review needs a goal in focus.", nameof(balance));

        List<ChatMessage> messages = [new(ChatRole.System, Instructions(balance, focus, routines, userName))];
        var response = await chatClient.GetResponseAsync<BalanceAnswer>(messages, cancellationToken: cancellationToken);
        var message = response.TryGetResult(out var answer) && !string.IsNullOrWhiteSpace(answer.Message)
            ? answer.Message.Trim()
            : Plain(balance, focus);

        return $"{message}\n\n{Question(focus.Name)}";
    }

    /// <summary>The message without the AI: what works first, then the goal in focus, with what was already done.</summary>
    internal static string Plain(BalanceDto balance, GoalBalanceDto focus)
    {
        var text = new StringBuilder();
        var thriving = balance.Thriving.Select(g => $"**{g.Name}**").ToList();
        if (thriving.Count > 0)
        {
            text.Append("Você está indo muito bem em ").Append(Join(thriving))
                .Append("! Manter essa frequência é uma conquista importante, e não vamos mexer no que está funcionando. ");
        }

        text.Append("Percebi que **").Append(focus.Name).Append("** está mais difícil de encaixar nas últimas semanas.");

        if (focus.TotalSoFar is { } total && focus.MainUnit is { } unit)
        {
            text.Append(" Mesmo assim, você já fez ").Append(GoalExtraction.Number(total)).Append(' ').Append(unit)
                .Append(", e isso tem valor.");
        }

        if (focus.Projection is { } projection)
        {
            text.Append(" Mantendo esse ritmo, a estimativa é chegar a cerca de ")
                .Append(GoalExtraction.Number(projection.Projected)).Append(' ').Append(projection.Unit)
                .Append(" até ").Append(projection.DueDate.ToString("dd/MM", PtBr)).Append(" (uma estimativa, não uma garantia).");
        }

        return text.ToString();
    }

    private static string Instructions(
        BalanceDto balance, GoalBalanceDto focus, IReadOnlyList<RoutineDto> routines, string? userName)
    {
        object Goal(GoalBalanceDto g) => new
        {
            meta = g.Name,
            situacao = g.Health switch
            {
                GoalHealth.Thriving => "funcionando bem (proteger)",
                GoalHealth.Struggling => "dificuldade recorrente",
                GoalHealth.Steady => "estável",
                _ => "pouco histórico",
            },
            diasFeitos = g.Done,
            diasNaoFeitos = g.Missed,
            diasSemConfirmacao = g.Unconfirmed,
            percentualFeito = g.DonePercent,
            percentualUltimas2Semanas = g.RecentPercent,
            percentual2SemanasAntes = g.EarlierPercent,
            totalJaFeito = g.TotalSoFar is null ? null : $"{GoalExtraction.Number(g.TotalSoFar.Value)} {g.MainUnit}",
            projecao = g.Projection is not { } p
                ? null
                : new
                {
                    jaFeito = p.SoFar,
                    projetadoAtePrazo = p.Projected,
                    alvo = p.Target,
                    unidade = p.Unit,
                    prazo = p.DueDate.ToString("yyyy-MM-dd"),
                },
        };

        var facts = new
        {
            periodo = new { de = balance.From.ToString("yyyy-MM-dd"), ate = balance.To.ToString("yyyy-MM-dd") },
            metaEmFoco = new
            {
                dados = Goal(focus),
                planoAtual = routines.Where(r => r.GoalId == focus.GoalId).Select(r => GoalPlan.Describe(GoalPlan.FromSaved(r))),
                motivosQueOUsuarioDeu = focus.MissReasons.Take(5),
            },
            metasFuncionandoBem = balance.Thriving.Select(Goal),
            outrasMetas = balance.Goals.Where(g => g.GoalId != focus.GoalId && g.Health != GoalHealth.Thriving).Select(Goal),
            sinais = balance.Signals.Select(s => s.Kind switch
            {
                BalanceSignal.RecurringDifficulty => "uma meta com dificuldade recorrente",
                BalanceSignal.SimultaneousDrop => "duas ou mais metas caíram nas mesmas semanas",
                _ => "várias faltas por falta de tempo ou de energia",
            }),
        };

        return $"""
            Você é o Pippin, o assistente do LifeAim: conversa em português, de forma leve, calorosa e natural, pelo
            Discord{(userName is null ? "" : $", com {userName}")}. Você vai puxar, por iniciativa própria, uma conversa
            sobre as prioridades do usuário, porque o histórico mostrou um sinal. Os fatos (JSON), já calculados:
            {JsonSerializer.Serialize(facts, Json)}

            Dias "sem confirmação" não são falhas e não entram nos percentuais. Os sinais são indícios, não diagnósticos.

            message: até 90 palavras, no estilo de chat:
            - Comece reconhecendo o que está funcionando (metasFuncionandoBem), com entusiasmo verdadeiro, e diga que não
              vamos mexer nisso. Se nenhuma estiver funcionando bem, valorize o que foi feito.
            - Depois fale da metaEmFoco com leveza ("percebi que a leitura ainda está mais difícil de encaixar"), sem
              culpa e sem cobrança. Não presuma a causa: não diga que é falta de tempo, preguiça ou desinteresse. Se o
              próprio usuário deu motivos, pode citá-los com as palavras dele.
            - Nunca compare os percentuais das metas entre si nem sugira igualá-los. Percentuais diferentes são naturais.
            - Reconheça o progresso absoluto (totalJaFeito). Se houver projecao, apresente-a sempre como estimativa
              condicionada ao ritmo atual ("mantendo esse ritmo, sua projeção é…"), nunca como garantia; se ficar abaixo do
              alvo, valorize o que ela representa como base para crescer, sem desmotivar.
            - Uma diferença pequena entre as duas quinzenas não é uma grande melhora nem uma grande piora: não exagere.
            - Não proponha mudanças ainda e não termine com pergunta: a pergunta final é acrescentada depois.
            - Use **negrito** no nome das metas, sem títulos e sem tabelas, no máximo dois emojis. Não invente números.
            """;
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} e {items[^1]}";

    internal sealed record BalanceAnswer(string? Message);
}
