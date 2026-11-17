using LifeAim.Modules.Fitness.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>The Fitness module's public API (the weight-loss journal) against a real, migrated PostgreSQL.</summary>
public sealed class FitnessJournalTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 7, 13);

    private readonly Guid _userId = Guid.CreateVersion7();

    private IFitnessJournal Journal => factory.Services.GetRequiredService<IFitnessJournal>();

    [Fact]
    public async Task The_profile_is_filled_little_by_little()
    {
        Assert.Null(await Journal.GetProfileAsync(_userId));

        await Journal.SaveProfileAsync(new SaveProfileRequest(_userId, HeightCm: 178, BirthYear: 1990, Sex: Sex.Male, TargetWeightKg: 84));
        var profile = await Journal.SaveProfileAsync(new SaveProfileRequest(_userId, DailyEnergyKcal: 2350, ProteinTargetG: 150, Notes: "treina 3x por semana"));

        Assert.Equal(
            (178m, 1990, Sex.Male, 84m, 2350, (int?)null, 150, "treina 3x por semana"),
            (profile.HeightCm, profile.BirthYear, profile.Sex, profile.TargetWeightKg, profile.DailyEnergyKcal,
                profile.CalorieTargetKcal, profile.ProteinTargetG, profile.Notes));
        Assert.Equal(profile, await Journal.GetProfileAsync(_userId));
    }

    [Fact]
    public async Task The_body_record_of_a_day_is_completed_and_a_value_told_again_replaces_the_earlier_one()
    {
        await Journal.RecordBodyAsync(new RecordBodyRequest(_userId, Day, WeightKg: 94.5m));
        await Journal.RecordBodyAsync(new RecordBodyRequest(_userId, Day, WaistCm: 110, FatPercent: 28.6m, BmrKcal: 1800));
        var record = await Journal.RecordBodyAsync(new RecordBodyRequest(_userId, Day, WeightKg: 94.25m, Note: "depois do café"));

        Assert.Equal(new BodyRecordDto(Day, 94.25m, 110, null, null, null, null, null, 28.6m, null, 1800, "depois do café"), record);
        Assert.Equal(record, (await Journal.GetDayAsync(_userId, Day)).Body); // one row for the day
    }

    [Fact]
    public async Task A_meal_keeps_its_items_and_the_day_adds_them_up()
    {
        await Journal.AddMealAsync(new AddMealRequest(_userId, Day, new TimeOnly(10, 0), "Café da manhã",
        [
            Item("Pão francês", 50, "g", 150, 150, 4, 4, NutritionSource.Estimate),
            Item("Whey protein", 30, "g", 118, 118, 23, 23, NutritionSource.FoodTable),
        ]));
        await Journal.AddMealAsync(new AddMealRequest(_userId, Day, new TimeOnly(13, 30), "Almoço",
        [
            Item("Frango cozido", 160, "g", 300, 380, 40, 48, NutritionSource.Estimate),
        ]));

        var day = await Journal.GetDayAsync(_userId, Day);

        Assert.Equal(["Café da manhã", "Almoço"], day.Meals.Select(m => m.Name));
        Assert.Equal((568m, 648m, 67m, 75m), (day.KcalMin, day.KcalMax, day.ProteinMinG, day.ProteinMaxG));
        var whey = day.Meals[0].Items[1];
        Assert.Equal(("Whey protein", 30m, "g", NutritionSource.FoodTable), (whey.Description, whey.Quantity, whey.Unit, whey.Source));
    }

    [Fact]
    public async Task Meals_come_in_the_order_they_were_eaten_with_the_ceia_after_midnight_last()
    {
        await Journal.AddMealAsync(new AddMealRequest(_userId, Day, new TimeOnly(0, 2), "Ceia", [Item("Barra", null, null, 69, 69, 1.3m, 1.3m)]));
        await Journal.AddMealAsync(new AddMealRequest(_userId, Day, null, "Jantar", [Item("Tapioca", 18, "g", 60, 65, 0, 0)]));
        await Journal.AddMealAsync(new AddMealRequest(_userId, Day, new TimeOnly(13, 30), "Almoço", [Item("Arroz", 100, "g", 128, 130, 2.5m, 2.5m)]));
        await Journal.AddMealAsync(new AddMealRequest(_userId, Day, new TimeOnly(10, 0), "Café da manhã", [Item("Pão", 50, "g", 150, 150, 4, 4)]));

        var meals = (await Journal.GetDayAsync(_userId, Day)).Meals;

        Assert.Equal(["Café da manhã", "Almoço", "Ceia", "Jantar"], meals.Select(m => m.Name)); // without a time: last
    }

    [Fact]
    public async Task Correcting_a_meal_replaces_its_items_and_keeps_what_was_not_told()
    {
        var meal = await Journal.AddMealAsync(new AddMealRequest(_userId, Day, null, "Café da tarde",
        [
            Item("Torrada", 2, "un", 70, 70, 2, 2),
            Item("Whey protein", 15, "g", 59, 59, 11.5m, 11.5m),
        ]));

        await Journal.UpdateMealAsync(new UpdateMealRequest(meal.Id, _userId, Items:
        [
            Item("Torrada", 3, "un", 105, 105, 3, 3),
            Item("Whey protein", 30, "g", 118, 118, 23, 23),
        ]));
        var updated = await Journal.UpdateMealAsync(new UpdateMealRequest(meal.Id, _userId, EatenAt: new TimeOnly(17, 15)));

        Assert.NotNull(updated);
        Assert.Equal(("Café da tarde", new TimeOnly(17, 15), 223m, 26m), (updated.Name, updated.EatenAt, updated.KcalMin, updated.ProteinMinG));
        Assert.Equal(updated, Assert.Single((await Journal.GetDayAsync(_userId, Day)).Meals), MealComparer.Instance);
    }

    [Fact]
    public async Task A_meal_of_someone_else_cannot_be_changed_or_deleted()
    {
        var meal = await Journal.AddMealAsync(new AddMealRequest(_userId, Day, null, "Lanche", [Item("Maçã", 1, "un", 70, 80, 0, 0)]));
        var stranger = Guid.CreateVersion7();

        Assert.Null(await Journal.UpdateMealAsync(new UpdateMealRequest(meal.Id, stranger, Name: "Outro")));
        Assert.False(await Journal.DeleteMealAsync(meal.Id, stranger));
        Assert.True((await Journal.GetDayAsync(stranger, Day)).IsEmpty);

        Assert.True(await Journal.DeleteMealAsync(meal.Id, _userId));
        Assert.Empty((await Journal.GetDayAsync(_userId, Day)).Meals);
    }

    [Fact]
    public async Task Values_out_of_the_possible_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Journal.RecordBodyAsync(new RecordBodyRequest(_userId, Day, WeightKg: 945)));
        await Assert.ThrowsAsync<ArgumentException>(() => Journal.RecordBodyAsync(new RecordBodyRequest(_userId, Day)));
        await Assert.ThrowsAsync<ArgumentException>(() => Journal.AddMealAsync(new AddMealRequest(_userId, Day, null, "Almoço", [])));
        await Assert.ThrowsAsync<ArgumentException>(() => Journal.AddMealAsync(
            new AddMealRequest(_userId, Day, null, "Almoço", [Item("Arroz", 100, "g", 130000, 130000, 2, 2)])));

        Assert.True((await Journal.GetDayAsync(_userId, Day)).IsEmpty);
    }

    [Fact]
    public async Task Exercises_and_notes_belong_to_their_day_and_the_days_come_in_order()
    {
        await Journal.AddExerciseAsync(new AddExerciseRequest(_userId, Day, new TimeOnly(22, 40), "Bicicleta ergométrica", 20, 200, 140, false));
        await Journal.AddExerciseAsync(new AddExerciseRequest(_userId, Day.AddDays(-1), null, "Elíptico", 60, 644, null, true));
        await Journal.AddDayNoteAsync(_userId, Day.AddDays(-2), "aniversário da esposa");
        await Journal.RecordBodyAsync(new RecordBodyRequest(_userId, Day, WeightKg: 94.5m));

        var days = await Journal.ListDaysAsync(_userId, Day.AddDays(-7), Day);

        Assert.Equal([Day.AddDays(-2), Day.AddDays(-1), Day], days.Select(d => d.Day));
        Assert.Equal(["aniversário da esposa"], days[0].Notes);
        Assert.Equal((644m, 644m, true), (days[1].ExerciseKcalMin, days[1].ExerciseKcalMax, days[1].Exercises[0].Measured));
        Assert.Equal((140m, 200m), (days[2].ExerciseKcalMin, days[2].ExerciseKcalMax)); // a range told backwards is put in order
        Assert.Equal(94.5m, days[2].Body?.WeightKg);

        Assert.True(await Journal.DeleteExerciseAsync(days[1].Exercises[0].Id, _userId));
        Assert.Equal(2, (await Journal.ListDaysAsync(_userId, Day.AddDays(-7), Day)).Count);
    }

    [Fact]
    public async Task The_food_table_keeps_one_food_per_name()
    {
        await Journal.SaveFoodAsync(new SaveFoodRequest(_userId, "Iogurte Grego Vigor Zero Morango", "1 pote de 90 g", 55, 5));
        await Journal.SaveFoodAsync(new SaveFoodRequest(_userId, "Whey Growth Chocolate", "30 g (1 scoop)", 118, 23, 3.8m, 1.9m));
        await Journal.SaveFoodAsync(new SaveFoodRequest(_userId, "iogurte grego vigor zero morango", "1 pote de 90 g", 51, 5.9m, 6.4m, 0.2m));

        var foods = await Journal.ListFoodsAsync(_userId);

        Assert.Equal(2, foods.Count);
        Assert.Equal((51m, 5.9m, 6.4m, 0.2m), (foods[0].Kcal, foods[0].ProteinG, foods[0].CarbsG, foods[0].FatG));
        Assert.Empty(await Journal.ListFoodsAsync(Guid.CreateVersion7()));
    }

    [Theory]
    [InlineData("arroz cozido", "Arroz, integral, cozido", 124, 2.6)]
    [InlineData("Pão francês", "Pão, trigo, francês", 300, 8.0)]
    [InlineData("ovos cozidos", "Ovo, de galinha, inteiro, cozido/10minutos", 146, 13.3)]
    [InlineData("100 g de feijão carioca cozido", "Feijão, carioca, cozido", 76, 4.8)]
    public async Task The_reference_table_finds_a_food_by_its_words_whatever_the_accents_and_plurals(
        string query, string expected, double kcal, double protein)
    {
        var foods = await Journal.SearchReferenceFoodsAsync(query);

        var food = Assert.Single(foods, f => f.Name == expected);
        Assert.Equal(((decimal)kcal, (decimal)protein), (food.KcalPer100g, food.ProteinG));
        Assert.True(foods.Count <= 8);
    }

    [Fact]
    public async Task The_reference_table_answers_nothing_to_nothing()
    {
        Assert.Empty(await Journal.SearchReferenceFoodsAsync("de 100 g"));
        Assert.Empty(await Journal.SearchReferenceFoodsAsync("xyzqw"));
        Assert.Null(await Journal.FindReferenceFoodAsync("arroz"));
        Assert.Equal(128, (await Journal.FindReferenceFoodAsync("ARROZ, tipo 1, cozido"))?.KcalPer100g);
    }

    [Fact]
    public async Task A_meal_item_can_come_from_the_reference_table()
    {
        await Journal.AddMealAsync(new AddMealRequest(_userId, Day, null, "Almoço",
            [Item("Arroz, tipo 1, cozido", 100, "g", 128, 128, 2.5m, 2.5m, NutritionSource.ReferenceTable)]));

        Assert.Equal(NutritionSource.ReferenceTable, (await Journal.GetDayAsync(_userId, Day)).Meals[0].Items[0].Source);
    }

    private static MealItemDto Item(
        string description, decimal? quantity, string? unit, decimal kcalMin, decimal kcalMax, decimal proteinMin, decimal proteinMax,
        NutritionSource source = NutritionSource.Estimate) =>
        new(description, quantity, unit, kcalMin, kcalMax, proteinMin, proteinMax, null, null, source);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        foreach (var table in new[] { "profiles", "body_records", "meals", "exercises", "foods", "day_notes" })
        {
            await using var command = dataSource.CreateCommand($"DELETE FROM fitness.{table} WHERE user_id = $1");
            command.Parameters.Add(new() { Value = _userId });
            await command.ExecuteNonQueryAsync();
        }
    }

    // Records compare lists by reference: a meal is the same when its fields and its items are.
    private sealed class MealComparer : IEqualityComparer<MealDto>
    {
        public static MealComparer Instance { get; } = new();

        public bool Equals(MealDto? x, MealDto? y) =>
            x is not null && y is not null && (x.Id, x.Day, x.EatenAt, x.Name) == (y.Id, y.Day, y.EatenAt, y.Name) && x.Items.SequenceEqual(y.Items);

        public int GetHashCode(MealDto meal) => meal.Id.GetHashCode();
    }
}
