using System.Text.Json;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit.Abstractions;

namespace LifeAim.IntegrationTests;

/// <summary>
/// The API with the REAL AI (OpenAI, the key from User Secrets) and the real PostgreSQL, but with the clock
/// fixed on 14/11/2026, so "até o natal" always means 25/12/2026.
/// </summary>
public sealed class RealAiApiFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Now = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Discord:Token"] = "" }));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedClock(Now)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// Pippin end to end with the real model: each message goes through IPippin exactly like a Discord DM,
/// and the asserts read what was saved in PostgreSQL. They cost tokens (a few cents per run) and the
/// model is not deterministic, so they check the rules (never a yes/no doubt, never save on an open
/// doubt, "1 livro em andamento" is not a start value), not the exact words.
/// They run only on request: <c>$env:LIFEAIM_REAL_AI=1; dotnet test --filter Category=RealAI</c> (see RealAi).
/// </summary>
[Trait("Category", "RealAI")]
public sealed class PippinRealAiTests(RealAiApiFactory factory, ITestOutputHelper output) : IClassFixture<RealAiApiFactory>, IAsyncLifetime
{
    private const string GoalsSentence =
        "vamos definir objetivos até o fim desse ano, estou com 94 kg e quero emagrecer até 90kg até o natal, " +
        "também estou no fim de 1 livro, mas quero ler mais 2 livros até o fim do ano";

    private readonly Guid _userId = Guid.CreateVersion7();

    private IPippin Pippin => factory.Services.GetRequiredService<IPippin>();

    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    [RealAiFact]
    public async Task Whole_conversation_from_hello_to_saved_goals()
    {
        Assert.Contains("Como você se chama?", await Send("Oi"));
        Assert.StartsWith("Prazer, Rafael dos Reis!", await Send("rafael dos reis"));

        var summary = await Send(GoalsSentence);

        var state = await StateAsync();
        Assert.Equal("ConfirmGoals", state.Stage);
        Assert.Equal("2026-12-31", state.Draft.GetProperty("horizon").GetString());
        AssertWeightGoal(state.Goals[0]);
        AssertBooksGoalWithoutStart(state.Goals[1]);
        AssertDoubtsAreChoices(state.Doubts, summary);

        if (state.Doubts.Count > 0)
        {
            // A "sim" does not choose an option: nothing is saved and the choice is asked again.
            var afterSim = await Send("sim");
            Assert.Empty(await GoalsAsync());
            Assert.NotEmpty((await StateAsync()).Doubts);
            Assert.Contains("**A)**", afterSim);

            // Choosing resolves the doubt, but still nothing is saved: the user must see the result first.
            var afterChoice = await Send(state.Doubts.Count == 1 ? "A" : "A e C");
            Assert.Empty(await GoalsAsync());
            Assert.Empty((await StateAsync()).Doubts);
            Assert.EndsWith("Responda **sim** para salvar, ou me diga o que quer mudar.", afterChoice);
        }

        Assert.StartsWith("Salvei! ✅", await Send("sim"));

        var saved = await GoalsAsync();
        Assert.Equal(2, saved.Count);
        var weight = Assert.Single(saved, g => g.Unit == "kg");
        Assert.Equal((94m, 90m, new DateOnly(2026, 12, 25)), (weight.StartValue, weight.TargetValue, weight.DueDate));
        var books = Assert.Single(saved, g => g.Unit != "kg");
        Assert.Null(books.StartValue);
        Assert.Contains(books.TargetValue, new decimal?[] { 2m, 3m });
        Assert.Equal(new DateOnly(2026, 12, 31), books.DueDate);

        Assert.Equal("CollectGoals", (await StateAsync()).Stage);
        Assert.StartsWith("Fechado!", await Send("por enquanto é só isso"));
    }

    [RealAiFact]
    public async Task Only_the_period_makes_pippin_ask_for_the_goals()
    {
        await SeedAsync("CollectGoals", draft: null);

        var reply = await Send("quero definir até o fim do ano");

        Assert.Contains("Quais metas você quer alcançar até 31/12/2026?", reply);
        Assert.Empty((await StateAsync()).Goals);
    }

    [RealAiFact]
    public async Task Another_subject_goes_back_to_the_pending_question()
    {
        await SeedAsync("CollectGoals", draft: null);

        var reply = await Send("qual vai ser a previsão do tempo amanhã?");

        Assert.Contains("Mas voltando ao que eu tinha perguntado: ", reply);
        Assert.Equal("CollectGoals", (await StateAsync()).Stage);
    }

    [RealAiTheory]
    [InlineData("sim")]
    [InlineData("ok")]
    [InlineData("pode ser")]
    public async Task An_answer_that_chooses_no_option_keeps_the_doubt_and_saves_nothing(string answer)
    {
        await SeedAsync("ConfirmGoals", BooksDraftWithDoubt);

        var reply = await Send(answer);

        Assert.Contains("**A)** 3 livros", reply);
        Assert.Single((await StateAsync()).Doubts);
        Assert.Empty(await GoalsAsync());
    }

    [RealAiTheory]
    [InlineData("A", 3)]
    [InlineData("b", 2)]
    [InlineData("a primeira opção", 3)]
    [InlineData("só os novos", 2)]
    public async Task Choosing_an_option_applies_it_and_then_sim_saves_it(string choice, int expectedTarget)
    {
        await SeedAsync("ConfirmGoals", BooksDraftWithDoubt);

        var reply = await Send(choice);

        var state = await StateAsync();
        Assert.Empty(state.Doubts);
        Assert.Equal(expectedTarget, state.Goals[0].GetProperty("targetValue").GetDecimal());
        Assert.EndsWith("Responda **sim** para salvar, ou me diga o que quer mudar.", reply);
        Assert.Empty(await GoalsAsync());

        Assert.StartsWith("Salvei! ✅", await Send("sim"));
        Assert.Equal(expectedTarget, Assert.Single(await GoalsAsync()).TargetValue);
    }

    // What Pippin would have shown for the books part of the sentence: one goal and a choice A/B.
    private const string BooksDraftWithDoubt =
        """
        {
          "horizon": "2026-12-31",
          "goals": [
            { "name": "Ler 2 livros", "description": "Está terminando um livro", "indicator": "livros terminados",
              "startValue": null, "targetValue": 2, "unit": "livros", "dueDate": "2026-12-31" }
          ],
          "doubts": [
            { "question": "Quantos livros a meta vai contar?",
              "options": ["3 livros: o que estou terminando + 2 novos", "2 livros: só os novos, sem contar o que estou terminando"] }
          ]
        }
        """;

    private static void AssertWeightGoal(JsonElement goal)
    {
        Assert.Equal(94m, goal.GetProperty("startValue").GetDecimal());
        Assert.Equal(90m, goal.GetProperty("targetValue").GetDecimal());
        Assert.Equal("kg", goal.GetProperty("unit").GetString());
        Assert.Equal("2026-12-25", goal.GetProperty("dueDate").GetString());
    }

    // The bug seen on Discord: "estou no fim de 1 livro" became startValue 1 ("de 1 para 2 livros").
    private static void AssertBooksGoalWithoutStart(JsonElement goal)
    {
        Assert.Equal(JsonValueKind.Null, goal.GetProperty("startValue").ValueKind);
        Assert.Contains(goal.GetProperty("targetValue").GetDecimal(), new[] { 2m, 3m });
        Assert.Equal("2026-12-31", goal.GetProperty("dueDate").GetString());
    }

    private static void AssertDoubtsAreChoices(IReadOnlyList<JsonElement> doubts, string reply)
    {
        foreach (var doubt in doubts)
        {
            var options = doubt.GetProperty("options").GetArrayLength();
            Assert.InRange(options, 2, 3);
            Assert.DoesNotContain("?", doubt.GetProperty("options").EnumerateArray().Select(o => o.GetString()).First());
        }

        if (doubts.Count > 0)
        {
            Assert.Contains("Antes de salvar, preciso que você escolha:", reply);
            Assert.DoesNotContain("**sim**", reply); // the choice comes first; "sim" only afterwards
        }
        else
        {
            Assert.EndsWith("Responda **sim** para salvar, ou me diga o que quer mudar.", reply);
        }
    }

    // Each exchange goes to the test output, to read what the model really answered.
    private async Task<string> Send(string text)
    {
        var reply = await Pippin.ReplyAsync(new PippinMessage(_userId, "teste", text));
        output.WriteLine($"> {text}\n{reply}\n");
        return reply;
    }

    private async Task<IReadOnlyList<GoalDto>> GoalsAsync() =>
        await factory.Services.GetRequiredService<IGoalService>().ListAsync(_userId);

    private sealed record State(string Stage, JsonElement Draft, IReadOnlyList<JsonElement> Goals, IReadOnlyList<JsonElement> Doubts);

    private async Task<State> StateAsync()
    {
        await using var command = Db.CreateCommand(
            "SELECT stage, coalesce(draft, '{}'::jsonb)::text FROM agents.conversations WHERE user_id = $1");
        command.Parameters.Add(new() { Value = _userId });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "a conversa não foi salva");

        var draft = JsonDocument.Parse(reader.GetString(1)).RootElement.Clone();
        return new State(reader.GetString(0), draft, Array(draft, "goals"), Array(draft, "doubts"));

        static IReadOnlyList<JsonElement> Array(JsonElement draft, string name) =>
            draft.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray()] : [];
    }

    // Puts the conversation at a stage directly, so a test spends tokens only on the step it checks.
    private async Task SeedAsync(string stage, string? draft)
    {
        await using var command = Db.CreateCommand(
            "INSERT INTO agents.conversations (user_id, stage, user_name, updated_at, draft) VALUES ($1, $2, 'Rafael', $3, $4)");
        command.Parameters.Add(new() { Value = _userId });
        command.Parameters.Add(new() { Value = stage });
        command.Parameters.Add(new() { Value = RealAiApiFactory.Now });
        command.Parameters.Add(new() { Value = (object?)draft ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });
        await command.ExecuteNonQueryAsync();
    }

    public Task InitializeAsync()
    {
        var key = factory.Services.GetRequiredService<IConfiguration>()["OpenAI:ApiKey"];
        Assert.False(string.IsNullOrWhiteSpace(key), "Estes testes usam a IA real: configure OpenAI:ApiKey nos User Secrets.");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await factory.Services.GetRequiredService<IConversationReset>().ResetAsync(_userId);
        await using var command = Db.CreateCommand("DELETE FROM goals.goals WHERE user_id = $1");
        command.Parameters.Add(new() { Value = _userId });
        await command.ExecuteNonQueryAsync();
    }
}
