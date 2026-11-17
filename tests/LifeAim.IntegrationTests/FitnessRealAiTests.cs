using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Fitness.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace LifeAim.IntegrationTests;

/// <summary>
/// The weight-loss conversation end to end with the real model and the real PostgreSQL: a day told to Pippin the way
/// it used to be told to a chat (weight, meals, a question, a correction, a workout), and the asserts read what ended
/// up in the journal. The model is not deterministic, so they check the rules (one meal per meal, a correction does
/// not duplicate, a question records nothing, totals in a plausible range), not the exact words or numbers.
/// They cost tokens: <c>$env:LIFEAIM_REAL_AI=1; dotnet test --filter Category=RealAI</c> (see RealAi).
/// The clock is RealAiApiFactory's: 14/11/2026, 12:00 in São Paulo.
/// </summary>
[Trait("Category", "RealAI")]
public sealed class FitnessRealAiTests(RealAiApiFactory factory, ITestOutputHelper output) : IClassFixture<RealAiApiFactory>, IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    private readonly Guid _userId = Guid.CreateVersion7();

    private IPippin Pippin => factory.Services.GetRequiredService<IPippin>();

    private IFitnessJournal Journal => factory.Services.GetRequiredService<IFitnessJournal>();

    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    [RealAiFact]
    public async Task A_day_told_by_conversation_ends_up_in_the_journal()
    {
        await Journal.SaveProfileAsync(new SaveProfileRequest(
            _userId, HeightCm: 178, BirthYear: 1990, Sex: Sex.Male, TargetWeightKg: 84, DailyEnergyKcal: 2350, ProteinTargetG: 150));
        await Journal.SaveFoodAsync(new SaveFoodRequest(_userId, "Whey Protein Growth Chocolate", "30 g (1 scoop)", 118, 23, 3.8m, 1.9m));

        // Weight and breakfast in one message, as people do.
        await Send("Estou com 94,5 kg. Café da manhã às 10:00: 13g de manteiga, 50g de pão francês e 30g de whey protein de chocolate");

        var day = await Journal.GetDayAsync(_userId, Today);
        Assert.Equal(94.5m, day.Body?.WeightKg);
        var breakfast = Assert.Single(day.Meals);
        Assert.Equal(new TimeOnly(10, 0), breakfast.EatenAt);
        Assert.Equal(3, breakfast.Items.Count);
        Assert.InRange(breakfast.KcalMin, 300, 420); // ~95 + ~150 + 118
        var whey = Assert.Single(breakfast.Items, i => i.Description.Contains("whey", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((118m, 118m, NutritionSource.FoodTable), (whey.KcalMin, whey.KcalMax, whey.Source)); // the user's table, not a guess

        // A question records nothing.
        var answer = await Send("Quantas torradas com manteiga eu posso comer no café da tarde?");
        Assert.Single((await Journal.GetDayAsync(_userId, Today)).Meals);
        Assert.Contains("kcal", answer);

        // A snack without a time, then its correction and its time: still one snack.
        await Send("Vou comer 2 torradas integrais sem manteiga e 15g de whey");
        await Send("Corrigindo, foram 30g de whey protein e 3 torradas");
        await Send("Foi às 11:40");

        day = await Journal.GetDayAsync(_userId, Today);
        Assert.Equal(2, day.Meals.Count);
        var snack = day.Meals[1];
        Assert.Equal(new TimeOnly(11, 40), snack.EatenAt);
        Assert.Equal(2, snack.Items.Count);
        Assert.Contains(snack.Items, i => i.Quantity == 3);
        Assert.Contains(snack.Items, i => i.Quantity == 30 && i.KcalMin == 118);

        // A product label goes to the user's table.
        await Send("O iogurte grego Vigor zero de morango tem 51 kcal e 5,9 g de proteína por pote de 90 g, guarda isso");
        Assert.Contains(await Journal.ListFoodsAsync(_userId), f => f.Kcal == 51 && f.ProteinG == 5.9m);

        // The weigh-in counted as progress of the weight goal, which nobody had to point at.
        var progress = await factory.Services.GetRequiredService<LifeAim.Modules.Progress.Contracts.IProgressService>()
            .ListAsync(_userId, Today, Today);
        Assert.Contains(progress, p => p is { Amount: 94.5m, Unit: "kg" });

        // A workout with the watch's calories.
        await Send("Fiz 30 min de elíptico agora, 332 kcal no relógio");
        var exercise = Assert.Single((await Journal.GetDayAsync(_userId, Today)).Exercises);
        Assert.Equal((30, 332m, true), (exercise.DurationMinutes, exercise.KcalMin, exercise.Measured));

        // The summary uses the journal's totals.
        var summary = await Send("Me dá o resumo do meu dia até agora");
        day = await Journal.GetDayAsync(_userId, Today);
        Assert.Equal(2, day.Meals.Count);
        Assert.InRange(day.KcalMin, 480, 750);
        Assert.Contains("94,5", summary);
    }

    [RealAiFact]
    public async Task The_photo_of_a_nutrition_label_goes_to_the_users_food_table()
    {
        var label = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "rotulo-ficticio.png"));

        await Send("guarda esse rótulo pra mim", new PippinImage(label, "image/png"));

        var food = Assert.Single(await Journal.ListFoodsAsync(_userId));
        Assert.Contains("Exemplo", food.Name);
        Assert.Contains("40", food.Serving);
        Assert.Equal((152m, 12m, 14m, 5.5m), (food.Kcal, food.ProteinG, food.CarbsG, food.FatG));

        // And it is used the next time the product is eaten.
        await Send("comi 1 barra proteica exemplo agora");
        var item = Assert.Single(Assert.Single((await Journal.GetDayAsync(_userId, Today)).Meals).Items);
        Assert.Equal((152m, NutritionSource.FoodTable), (item.KcalMin, item.Source));
    }

    private async Task<string> Send(string text, params PippinImage[] images)
    {
        var reply = await Pippin.ReplyAsync(new PippinMessage(_userId, "teste", text, images));
        output.WriteLine($"> {text}\n{reply}\n");
        return reply;
    }

    // Between tasks, with a weight goal already saved: where the user is on an ordinary day.
    public async Task InitializeAsync()
    {
        await using (var conversation = Db.CreateCommand(
                         "INSERT INTO agents.conversations (user_id, stage, user_name, updated_at) VALUES ($1, 'CollectGoals', 'Rafael', $2)"))
        {
            conversation.Parameters.Add(new() { Value = _userId });
            conversation.Parameters.Add(new() { Value = RealAiApiFactory.Now.AddHours(-1) });
            await conversation.ExecuteNonQueryAsync();
        }

        await using var goal = Db.CreateCommand("INSERT INTO goals.goals (id, user_id, name, created_at) VALUES ($1, $2, $3, $4)");
        goal.Parameters.Add(new() { Value = Guid.CreateVersion7() });
        goal.Parameters.Add(new() { Value = _userId });
        goal.Parameters.Add(new() { Value = "Chegar a menos de 84 kg" });
        goal.Parameters.Add(new() { Value = RealAiApiFactory.Now.AddDays(-7) });
        await goal.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await factory.Services.GetRequiredService<IConversationReset>().ResetAsync(_userId);
        // One command per statement: PostgreSQL does not take several statements with $1 parameters in one command.
        foreach (var table in new[]
                 {
                     "goals.goals", "progress.progress_entries", "progress.check_ins", "fitness.profiles", "fitness.body_records",
                     "fitness.meals", "fitness.exercises", "fitness.foods", "fitness.day_notes",
                 })
        {
            await using var command = Db.CreateCommand($"DELETE FROM {table} WHERE user_id = $1");
            command.Parameters.Add(new() { Value = _userId });
            await command.ExecuteNonQueryAsync();
        }
    }
}
