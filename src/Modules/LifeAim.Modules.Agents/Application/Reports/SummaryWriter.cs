using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Reports;

/// <summary>
/// Writes the monthly summary (Funcionalidade 4) and, when the history shows a real pattern, the insight invitation
/// and the insight itself. The numbers come counted from the Reporting module (the AI never adds them up); ONE AI
/// call turns them into a short, encouraging message. If its answer cannot be read, a plain summary goes instead.
/// </summary>
internal sealed class SummaryWriter(IChatClient chatClient)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Fewer done days than this and no pattern is worth an insight.</summary>
    public const int MinDoneDaysForInsight = 8;

    public async Task<WrittenSummary> WriteAsync(
        MonthlyReportDto report, string? userName, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages = [new(ChatRole.System, Instructions(report, userName))];
        var response = await chatClient.GetResponseAsync<SummaryAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer) || string.IsNullOrWhiteSpace(answer.Message))
        {
            return new WrittenSummary(Plain(report), null, null);
        }

        // The invitation only with something to reveal, and only when the history is long enough to show a pattern.
        var hasInsight = !string.IsNullOrWhiteSpace(answer.InsightTeaser)
                         && !string.IsNullOrWhiteSpace(answer.Insight)
                         && report.Month.Goals.Any(g => g.Done >= MinDoneDaysForInsight);

        return new WrittenSummary(
            answer.Message.Trim(),
            hasInsight ? answer.InsightTeaser!.Trim() : null,
            hasInsight ? answer.Insight!.Trim() : null);
    }

    /// <summary>The summary without the AI.</summary>
    internal static string Plain(MonthlyReportDto report)
    {
        var month = MonthName(report.Month.From);
        var known = report.Month.Done + report.Month.Missed;
        var text = report.Month.DonePercent is { } percent
            ? $"Resumo de {month}: você fez {percent}% do que foi proposto ({report.Month.Done} de {known} dias com resultado conhecido). Consistência é o que faz a diferença! 💪"
            : $"Resumo de {month}: você registrou atividade em {report.Month.Done} dia(s). Consistência é o que faz a diferença! 💪";

        return report.Quarter is { DonePercent: { } quarter }
            ? $"{text}\nNo trimestre, você cumpriu {quarter}% do que teve resultado conhecido."
            : text;
    }

    internal static string MonthName(DateOnly month) => month.ToString("MMMM", PtBr);

    private static string Instructions(MonthlyReportDto report, string? userName)
    {
        object Period(PeriodStatsDto p) => new
        {
            de = p.From.ToString("yyyy-MM-dd"),
            ate = p.To.ToString("yyyy-MM-dd"),
            feitos = p.Done,
            naoFeitos = p.Missed,
            semConfirmacao = p.Unconfirmed,
            percentualFeito = p.DonePercent,
            metas = p.Goals.Select(g => new
            {
                meta = g.Name,
                feitos = g.Done,
                naoFeitos = g.Missed,
                semConfirmacao = g.Unconfirmed,
                percentualFeito = g.DonePercent,
                totais = g.Totals.Select(t => new { unidade = t.Unit, total = t.Total }),
                porDiaDaSemana = g.MainUnit is null && g.Done == 0
                    ? null
                    : g.ByWeekday.Select(d => new { dia = DayName(d.IsoDay), diasFeitos = d.DoneDays, quantidade = d.Amount, unidade = g.MainUnit }),
                alvo = g.TargetValue,
                unidadeDoAlvo = g.Unit,
                prazo = g.DueDate?.ToString("yyyy-MM-dd"),
            }),
        };

        var facts = new
        {
            mes = MonthName(report.Month.From),
            resumoDoMes = Period(report.Month),
            mesAnterior = new { mes = MonthName(report.Previous.From), percentualFeito = report.Previous.DonePercent, feitos = report.Previous.Done },
            trimestre = report.Quarter is null ? null : Period(report.Quarter),
            mesesDoTrimestre = report.QuarterMonths.Select(m => new { mes = MonthName(m.From), percentualFeito = m.DonePercent, feitos = m.Done }),
        };

        var quarterRules = report.Quarter is null
            ? "- Não há panorama trimestral este mês: fale só do mês."
            : """
              - Este mês fecha um trimestre: depois do mês, inclua um panorama curto do trimestre (trimestre e
                mesesDoTrimestre): tendência entre os meses, evolução das metas e como o ano está avançando. Se fizer
                sentido, uma projeção até o prazo da meta, só quando a unidade dos totais for a mesma do alvo, sempre como
                estimativa ("mantendo esse ritmo, sua projeção é…"), nunca como garantia.
              """;

        return $"""
            Você é o Pippin, o assistente do LifeAim: conversa em português, de forma leve, calorosa e natural, pelo
            Discord{(userName is null ? "" : $", com {userName}")}. Você vai mandar, por iniciativa própria, o resumo do
            mês que terminou. Os números (JSON), já contados:
            {JsonSerializer.Serialize(facts, Json)}

            feitos, naoFeitos e semConfirmacao são dias. percentualFeito considera só os dias com resultado conhecido
            (feitos ÷ (feitos + naoFeitos)); os sem confirmação não contam nem como feitos nem como falhas.

            message: o resumo, no estilo de chat, até {(report.Quarter is null ? 90 : 160)} palavras:
            - Comece pelo percentual do mês e uma frase positiva e encorajadora (ex.: "Você fez 80% do que foi proposto
              para este mês, isso é bom demais! Consistência é o que faz a diferença."). Sem percentual, fale do que foi feito.
            - Cite as metas com o que importa (use **negrito** no nome delas), valorizando a consistência e o progresso real.
              Uma diferença pequena em relação ao mês anterior não é uma grande melhora: não exagere, mas reconheça o que
              foi construído. Nunca culpe nem cobre.
            - Se houver dias sem confirmação, pode dizer que eles não entraram na conta.
            {quarterRules}
            - Não invente números além dos acima. Sem títulos (#) e sem tabelas; listas curtas são permitidas. No máximo dois emojis.
            - Não termine com pergunta.

            insightTeaser e insight: só se os dados mostrarem um padrão real e relevante de uma meta (por exemplo, rende
            muito mais em certos dias da semana), com pelo menos {MinDoneDaysForInsight} dias feitos nela. Senão, os dois null.
            - insightTeaser: um convite curto e envolvente que cite o hábito específico e termine com "Quer saber qual?"
              (ex.: "Tive um insight insano sobre seu hábito de leitura. Pode te ajudar. Quer saber qual?").
            - insight: até 80 palavras, revelado só se ele aceitar. Explique o padrão que os números mostram, como uma
              observação (não uma conclusão definitiva), e sugira uma ação prática que respeite o plano atual (ex.: "Você
              tende a ler 3 vezes mais páginas nos fins de semana do que nos dias de semana. Vale continuar lendo durante
              a semana para manter a rotina, mas também podemos aproveitar seu ritmo maior nos fins de semana.").
            """;
    }

    private static string DayName(int isoDay) =>
        isoDay switch
        {
            1 => "segunda",
            2 => "terça",
            3 => "quarta",
            4 => "quinta",
            5 => "sexta",
            6 => "sábado",
            _ => "domingo",
        };

    internal sealed record SummaryAnswer(string? Message, string? InsightTeaser, string? Insight);
}

/// <param name="InsightTeaser">The invitation sent at the end of the summary, or null when there is no insight.</param>
internal sealed record WrittenSummary(string Message, string? InsightTeaser, string? Insight)
{
    /// <summary>What goes to the chat: the summary, then the invitation.</summary>
    public string Text => InsightTeaser is null ? Message : $"{Message}\n\n{InsightTeaser}";
}
