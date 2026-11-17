using System.Globalization;
using LifeAim.Modules.Fitness.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Fitness;

/// <summary>
/// The words of the journal's reminders. The simple ones are fixed text (no AI call for "já se pesou?"); only the
/// summary of the day is written by the AI, from the totals the code added up.
/// </summary>
internal sealed class FitnessReminderWriter(IChatClient chatClient)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public const string WeighIn = "⚖️ Já se pesou hoje? Me manda o peso quando puder.";

    public const string NoRecord = "🍽️ Ainda não registrei nenhuma refeição sua hoje. Me conta o que você comeu até agora?";

    public static string Custom(string text) => $"⏰ Lembrete: {text}. Já fez hoje?";

    /// <summary>One message for everything due now, the summary of the day (when due) last.</summary>
    public async Task<string> WriteAsync(
        IReadOnlyList<ReminderDto> due,
        FitnessDayDto day,
        IReadOnlyList<FitnessDayDto> recentDays,
        FitnessProfileDto? profile,
        string? userName,
        CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        foreach (var reminder in due.Where(r => r.Kind != ReminderKind.DayClose))
        {
            parts.Add(reminder.Kind switch
            {
                ReminderKind.WeighIn => WeighIn,
                ReminderKind.NoRecord => NoRecord,
                _ => Custom(reminder.Text ?? "o que combinamos"),
            });
        }

        if (due.Any(r => r.Kind == ReminderKind.DayClose))
        {
            parts.Add(await DayCloseAsync(day, recentDays, profile, userName, cancellationToken));
        }

        return string.Join("\n\n", parts);
    }

    private async Task<string> DayCloseAsync(
        FitnessDayDto day,
        IReadOnlyList<FitnessDayDto> recentDays,
        FitnessProfileDto? profile,
        string? userName,
        CancellationToken cancellationToken)
    {
        var facts = FitnessText.Day(day, profile);
        var trend = FitnessText.WeightTrend(recentDays, day.Day, profile);

        var instructions = $"""
            Você é o Pippin, o assistente do LifeAim, parceiro de emagrecimento{(userName is null ? "" : $" de {userName}")}. Escreva, em
            português, o FECHAMENTO DO DIA {day.Day.ToString("dd/MM", PtBr)} que você envia por conta própria pelo Discord.

            O que foi registrado (os totais já estão somados; use estes números, não some nem invente):
            {facts}

            {trend ?? "Sem pesagens recentes."}

            Formato, curto (até ~110 palavras):
            - Primeira linha: "**Fechamento do dia**" e a data.
            - "- **Ingerido:** ..." · "- **Proteína:** ..." · "- **Treino:** ..." (só se houve) · "- **Saldo:** ..." (só se o
              texto acima traz o gasto estimado: diga déficit, manutenção ou superávit, com a faixa; o número "se o dia
              terminasse agora" é o saldo do dia, porque o dia terminou).
            - Uma frase com o que foi melhor no dia e uma com o ajuste mais útil para amanhã, concreto e com número
              quando der. Dia acima do planejado não é fracasso: sem culpa, a conduta é voltar ao padrão amanhã.
            - Se alguma refeição importante parece faltar (por exemplo, não há jantar), pergunte em uma frase se ele
              ainda vai registrar; senão, não faça perguntas.
            No máximo dois emojis, sem tabelas e sem títulos (#).
            """;

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.System, instructions), new ChatMessage(ChatRole.User, "Escreva o fechamento do dia.")],
            cancellationToken: cancellationToken);

        var text = response.Text.Trim();
        // The model answered nothing: the plain totals are still worth sending.
        return text.Length > 0 ? text : $"**Fechamento do dia** — {day.Day.ToString("dd/MM", PtBr)}\n{FitnessText.Totals(day, profile)}";
    }
}
