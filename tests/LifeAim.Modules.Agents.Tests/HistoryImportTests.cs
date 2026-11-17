using LifeAim.Importer;
using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>
/// The importer of a journal kept by hand. The sample below is made up, in the format of the real file: nobody's
/// personal records live in the repository.
/// </summary>
public class HistoryImportTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    private const string Sample = """
        ARQUIVO CONSOLIDADO

        ========================================================================
        1. REGISTRO DE ALIMENTAÇÃO
        ========================================================================
        Finalidade: histórico diário.

        Registro – 30/12/2025 -----------------------------------------

        09:00
         - Pão francês 50 g
         - 30 g de whey protein

        TOTAL DO DIA: ~270 kcal

        📅 Divisão sugerida para o dia 31/12
        Calorias alvo: 1.800 kcal

        📅 Resumo — 31/12 -----------------------------

        13:00 - Almoço
        Arroz 100 g, feijão 100 g

        Resumo nutricional do dia (estimado)
        Calorias: ~200 – 220 kcal

        Resumo — 02/01 -----------------------------

        20:00 - Jantar
        Pizza 2 pedaços

        Total do dia: ~1.000 kcal

        ========================================================================
        2. REGISTRO DE EMAGRECIMENTO
        ========================================================================
        Finalidade: evolução corporal.

        Idade: 30 anos
        Altura: 1,80
        Objetivo: Chegar a menos de 80 kg
        Rotina semanal: Academia 3 vezes por semana
        Restrições alimentares: Nenhuma.

        Dia 30/12 - Terça:
        Peso atual: 90,3 kg
        Circunferência abdominal: 100cm

        Dia 02/01
        Peso: 89,75 kgkg
        Circunferência abdominal: 99,5 cm
        Circunferência quadril: não medido
        Circunferência peitoral: 104 cm
        Bioimpedância do smartwatch:
        35,1kg de músculo esquelético
        24,9kg de massa gorda
        47,7kg de Água Corporal
        27,7% de Gordura Corporal
        27,7 de IMC
        1.777 kcal de Taxa de metabolismo basal.
        Obs.: dormi mal.
        depois de um dia de festa

        ========================================================================
        3. TABELA NUTRICIONAL DE ALIMENTOS
        ========================================================================
        Iogurte Exemplo Zero 90g: 51 kcal, 5,9 g de proteína
        """;

    private readonly FakeFitnessJournal _journal = new();
    private readonly StringWriter _output = new();

    [Fact]
    public void The_file_is_cut_into_its_sections_and_days_and_a_date_without_a_year_follows_the_calendar()
    {
        var sections = HistoryFile.Split(Sample);
        var days = HistoryFile.Days(sections.Meals, 2025);

        Assert.Contains("Iogurte Exemplo", sections.Foods);
        Assert.DoesNotContain("Peso", sections.Meals);
        Assert.Equal([new DateOnly(2025, 12, 30), new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 2)], days.Select(d => d.Day));
        Assert.Contains("Divisão sugerida", days[0].Text); // a suggestion for the next day is not a new day
        Assert.Contains("Pizza", days[2].Text);
    }

    [Fact]
    public void The_body_section_is_read_by_code_with_decimals_thousands_and_the_persons_remarks()
    {
        var body = HistoryFile.Split(Sample).Body;

        var records = HistoryFile.BodyRecords(body, UserId, 2025);
        var preamble = HistoryFile.ReadPreamble(body);

        Assert.Equal(
            [
                new RecordBodyRequest(UserId, new DateOnly(2025, 12, 30), WeightKg: 90.3m, WaistCm: 100),
                new RecordBodyRequest(
                    UserId, new DateOnly(2026, 1, 2), WeightKg: 89.75m, WaistCm: 99.5m, ChestCm: 104, MuscleKg: 35.1m, FatKg: 24.9m,
                    WaterKg: 47.7m, FatPercent: 27.7m, Bmi: 27.7m, BmrKcal: 1777, Note: "dormi mal. depois de um dia de festa"),
            ],
            records);
        Assert.Equal((180m, 30, 80m), (preamble.HeightCm, preamble.AgeYears, preamble.TargetWeightKg));
        Assert.Contains("Academia 3 vezes", preamble.Notes);
    }

    [Fact]
    public async Task A_simulation_shows_everything_and_saves_nothing()
    {
        var report = await Importer(Days()).RunAsync(Sample, new ImportOptions(UserId, Apply: false, Only: null, FirstYear: 2025), CancellationToken.None);

        Assert.Equal((2, 1, 2), (report.BodyRecords, report.Foods, report.Days));
        Assert.Empty(_journal.Bodies);
        Assert.Empty(_journal.Meals);
        Assert.Empty(_journal.Foods);
        Assert.StartsWith("== SIMULAÇÃO", _output.ToString());
    }

    [Fact]
    public async Task Applying_saves_the_days_that_match_the_files_own_totals_and_reports_the_others()
    {
        var report = await Importer(Days()).RunAsync(Sample, new ImportOptions(UserId, Apply: true, Only: null, FirstYear: 2025), CancellationToken.None);

        Assert.Equal([new DateOnly(2026, 1, 2)], report.DivergentDays); // 400 kcal transcribed against ~1.000 stated
        Assert.Equal([new DateOnly(2025, 12, 30), new DateOnly(2025, 12, 31)], _journal.Meals.Select(m => m.Meal.Day));
        Assert.Equal(new TimeOnly(9, 0), _journal.Meals[0].Meal.EatenAt);
        Assert.Equal((268m, 27m), (_journal.Meals[0].Meal.KcalMin, _journal.Meals[0].Meal.ProteinMinG));
        Assert.Equal("Musculação", Assert.Single(_journal.Exercises).Exercise.Activity);
        Assert.Equal(2, _journal.Bodies.Count);
        Assert.Equal((180m, 1995, 80m), (_journal.Profiles[UserId].HeightCm, _journal.Profiles[UserId].BirthYear, _journal.Profiles[UserId].TargetWeightKg));
        Assert.Equal(51, Assert.Single(_journal.Foods).Food.Kcal);
        Assert.Contains("02/01/2026: 1 refeições, 400–400 kcal (arquivo: 1.000–1.000) — DIVERGENTE, não importado", _output.ToString());
    }

    [Fact]
    public async Task Running_it_again_duplicates_nothing()
    {
        var options = new ImportOptions(UserId, Apply: true, Only: null, FirstYear: 2025);
        await Importer(Days()).RunAsync(Sample, options, CancellationToken.None);

        // The two days already in the journal are not even read again: the AI is asked only for the table and the
        // day that was left out, which is still too far from its total.
        var ai = new FakeChatClient(
            """{ "foods": [ { "name": "iogurte exemplo zero", "serving": "1 pote de 90 g", "kcal": 51, "proteinG": 5.9 } ] }""",
            """{ "meals": [ { "name": "Jantar", "items": [ { "description": "Pizza", "kcal": 400, "proteinG": 16 } ] } ], "declaredKcalMin": 1000, "declaredKcalMax": 1000 }""");
        var again = await Importer(ai).RunAsync(Sample, options, CancellationToken.None);

        Assert.Equal(2, ai.Calls.Count);

        Assert.Equal([new DateOnly(2025, 12, 30), new DateOnly(2025, 12, 31)], again.SkippedDays);
        Assert.Equal(2, _journal.Meals.Count);
        Assert.Equal(2, _journal.Bodies.Count);
        Assert.Single(_journal.Foods);
        Assert.Single(_journal.Exercises);
    }

    [Fact]
    public async Task One_section_can_be_imported_alone_and_the_body_needs_no_ai()
    {
        var ai = new FakeChatClient("not used");

        var report = await Importer(ai).RunAsync(Sample, new ImportOptions(UserId, Apply: true, Only: "body", FirstYear: 2025), CancellationToken.None);

        Assert.Equal((2, 0, 0), (report.BodyRecords, report.Foods, report.Days));
        Assert.Empty(ai.Calls);
    }

    private HistoryImporter Importer(FakeChatClient ai) => new(_journal, new HistoryExtractor(ai), _output);

    // The AI's transcriptions, in the order the importer asks: the nutrition table, then each day.
    private static FakeChatClient Days() => new(
        """{ "foods": [ { "name": "Iogurte Exemplo Zero", "serving": "1 pote de 90 g", "kcal": 51, "proteinG": 5.9 } ] }""",
        """
        { "meals": [ { "name": "Café da manhã", "time": "09:00", "items": [
              { "description": "Pão francês", "quantity": 50, "unit": "g", "kcal": 150, "proteinG": 4 },
              { "description": "Whey protein", "quantity": 30, "unit": "g", "kcal": 118, "proteinG": 23 } ] } ],
          "exercises": [ { "activity": "Musculação", "durationMinutes": 40, "measured": false } ],
          "notes": [ "registro por memória" ], "declaredKcalMin": 270, "declaredKcalMax": 270 }
        """,
        """
        { "meals": [ { "name": "Almoço", "time": "13:00", "items": [
              { "description": "Arroz", "quantity": 100, "unit": "g", "kcal": 128, "proteinG": 2.5 },
              { "description": "Feijão", "quantity": 100, "unit": "g", "kcal": 76, "proteinG": 4.8 } ] } ],
          "declaredKcalMin": 200, "declaredKcalMax": 220 }
        """,
        """
        { "meals": [ { "name": "Jantar", "time": "20:00", "items": [
              { "description": "Pizza", "quantity": 2, "unit": "pedaços", "kcal": 400, "proteinG": 16 } ] } ],
          "declaredKcalMin": 1000, "declaredKcalMax": 1000 }
        """);
}
