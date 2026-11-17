using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace LifeAim.Importer;

/// <summary>
/// The free-text parts of the old journal, read by the AI into the journal's shapes: one call per day of meals, one
/// for the nutrition table. It only transcribes; the importer checks each day against the totals the file itself
/// states before anything is saved.
/// </summary>
internal sealed class HistoryExtractor(IChatClient chatClient)
{
    public async Task<ExtractedDay?> DayAsync(HistoryFile.DayText day, string foodTable, CancellationToken cancellationToken)
    {
        var instructions = $"""
            Você transcreve um diário alimentar antigo, escrito à mão em português, para dados estruturados. O texto
            abaixo é o registro do dia {day.Day:dd/MM/yyyy}. Transcreva só o que está escrito; não invente refeições,
            alimentos nem treinos.

            - meals: uma por horário/refeição, na ordem do texto. name ("Café da manhã", "Almoço", "Lanche", "Jantar",
              "Ceia"...; sem nome no texto, escolha pelo horário). time "HH:mm" ou null ("horário não informado").
              Coisas sem caloria e sem valor (chiclete, "café com adoçante" sozinho) podem ficar de fora.
            - items: um por alimento. description como no texto; quantity e unit como escritos (50 e "g"; 2 e "un"),
              null se o texto não diz. kcal e proteinG DAQUELA quantidade:
              - se o texto dá o valor do item ou da refeição, respeite-o (divida o total da refeição entre os itens);
              - se o alimento está na tabela do usuário abaixo, use-a, proporcional à quantidade;
              - senão, estime com valores típicos do alimento pronto. Use kcalMax/proteinMaxG quando houver faixa.
              A soma dos itens do dia deve ficar dentro do total que o próprio texto declara, quando ele declara.
            - exercises: cada atividade ("Musculação - Pernas", "Esteira 5 km/h 9% inclinação"), durationMinutes, time
              se houver, kcal/kcalMax se o texto der o gasto (measured true só se disser relógio/smartwatch).
            - notes: observações curtas do dia que explicam os números ("dia social: aniversário", "sem treino:
              resfriado", "registro por memória", "vitamina tomada"). Não copie análises nem recomendações.
            - declaredKcalMin / declaredKcalMax: o total de calorias INGERIDAS do dia que o texto declara ("TOTAL DO
              DIA: ~1.620 kcal" → 1620 e 1620; "~1.910 – 2.040 kcal" → 1910 e 2040; se houver um total "final
              considerado" ou "ajustado", use esse). null se o texto não declara total.

            Tabela nutricional do usuário:
            {(foodTable.Length == 0 ? "(vazia)" : foodTable)}
            """;

        var response = await chatClient.GetResponseAsync<ExtractedDay>(
            [new ChatMessage(ChatRole.System, instructions), new ChatMessage(ChatRole.User, day.Text)],
            cancellationToken: cancellationToken);
        return response.TryGetResult(out var extracted) ? extracted : null;
    }

    public async Task<IReadOnlyList<ExtractedFood>> FoodsAsync(string foodsSection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(foodsSection))
        {
            return [];
        }

        const string Instructions = """
            Você transcreve uma tabela nutricional pessoal, escrita à mão em português, para dados estruturados: um
            item por alimento ou produto. name com marca e sabor; serving é a porção a que os valores se referem, como
            escrita ("30 g (1 scoop)", "1 pote de 90 g", "1 unidade"); kcal, proteinG, carbsG e fatG dessa porção (null
            quando o texto não dá). Transcreva só o que está escrito.
            """;

        var response = await chatClient.GetResponseAsync<ExtractedFoods>(
            [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, foodsSection)],
            cancellationToken: cancellationToken);
        return response.TryGetResult(out var extracted) ? extracted.Foods ?? [] : [];
    }
}

internal sealed record ExtractedDay(
    List<ExtractedMeal>? Meals,
    List<ExtractedExercise>? Exercises,
    List<string>? Notes,
    decimal? DeclaredKcalMin,
    decimal? DeclaredKcalMax);

internal sealed record ExtractedMeal(string? Name, string? Time, List<ExtractedItem>? Items);

internal sealed record ExtractedItem(
    string? Description,
    decimal? Quantity,
    string? Unit,
    [property: Description("kcal desta quantidade: o valor, ou o mínimo da faixa.")] decimal Kcal,
    decimal? KcalMax,
    [property: Description("Proteína (g) desta quantidade: o valor, ou o mínimo da faixa.")] decimal ProteinG,
    decimal? ProteinMaxG);

internal sealed record ExtractedExercise(string? Activity, int? DurationMinutes, string? Time, decimal? Kcal, decimal? KcalMax, bool Measured);

internal sealed record ExtractedFoods(List<ExtractedFood>? Foods);

internal sealed record ExtractedFood(string? Name, string? Serving, decimal? Kcal, decimal? ProteinG, decimal? CarbsG, decimal? FatG);
