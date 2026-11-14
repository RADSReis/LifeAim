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

    private const string PeriodOnly = """{ "horizon": "2026-12-31", "goals": [], "doubts": [] }""";

    [RealAiTheory]
    [InlineData("olá")]
    [InlineData("bom dia!")]
    [InlineData("e aí, tudo bem?")]
    public async Task A_greeting_lists_the_goals_instead_of_going_to_the_router(string greeting)
    {
        await SeedAsync("CollectGoals", PeriodOnly);
        await SeedGoalAsync("Emagrecer até 90 kg", RealAiApiFactory.Now.AddDays(-1));
        await SeedGoalAsync("Ler 2 livros", RealAiApiFactory.Now.AddDays(-1).AddMinutes(1));

        var reply = await Send(greeting);

        Assert.Equal(
            "Opa! Olá, Rafael! 😊\n\n**Suas metas:**\n- Emagrecer até 90 kg\n- Ler 2 livros\n\n" +
            "Quer adicionar mais alguma meta até 31/12/2026?",
            reply);
    }

    [RealAiFact]
    public async Task A_greeting_while_waiting_for_the_name_asks_it_again()
    {
        await SeedAsync("AskName", draft: null);
        await using (var noName = Db.CreateCommand("UPDATE agents.conversations SET user_name = NULL WHERE user_id = $1"))
        {
            noName.Parameters.Add(new() { Value = _userId });
            await noName.ExecuteNonQueryAsync();
        }

        Assert.Equal("Oi! 😊 Como você se chama?", await Send("oi"));
    }

    [RealAiFact]
    public async Task A_greeting_while_confirming_repeats_the_choice_and_saves_nothing()
    {
        await SeedAsync("ConfirmGoals", BooksDraftWithDoubt);

        var reply = await Send("oi");

        Assert.StartsWith("Opa! Olá, Rafael! 😊 Voltando de onde paramos:", reply);
        Assert.Contains("**A)** 3 livros", reply);
        Assert.Single((await StateAsync()).Doubts);
        Assert.Empty(await GoalsAsync());
    }

    [RealAiFact]
    public async Task A_greeting_with_a_goal_is_about_the_goal()
    {
        await SeedAsync("CollectGoals", PeriodOnly);

        var reply = await Send("oi! quero correr 5 km até o fim do ano");

        Assert.Equal("ConfirmGoals", (await StateAsync()).Stage);
        Assert.Contains("5", reply);
    }

    // ---------- ⑤ Detailing ----------

    [RealAiFact]
    public async Task A_vague_goal_is_detailed_after_sim_and_the_second_sim_saves_the_proposal()
    {
        await SeedAsync("ConfirmGoals",
            """{ "horizon": "2026-12-31", "goals": [{ "name": "Ler mais" }], "doubts": [] }""");

        var proposal = await Send("sim");

        // Saved as said, then a complete proposal is shown and waits for confirmation.
        var state = await StateAsync();
        Assert.Equal("DetailGoals", state.Stage);
        Assert.Contains("Vamos detalhar", proposal);
        var proposed = Assert.Single(state.Goals);
        Assert.NotEqual(JsonValueKind.Null, proposed.GetProperty("indicator").ValueKind);
        Assert.True(proposed.GetProperty("targetValue").GetDecimal() > 0);
        Assert.NotEqual(JsonValueKind.Null, proposed.GetProperty("dueDate").ValueKind);
        Assert.Null(Assert.Single(await GoalsAsync()).Indicator);

        var summary = await Send("sim");

        var goal = Assert.Single(await GoalsAsync());
        Assert.NotNull(goal.Indicator);
        Assert.NotNull(goal.TargetValue);
        Assert.NotNull(goal.DueDate);
        Assert.Contains("Pronto, terminamos de detalhar!", summary);
        Assert.Equal("CollectGoals", (await StateAsync()).Stage);
    }

    [RealAiFact]
    public async Task A_correction_while_detailing_changes_the_proposal_but_saves_nothing()
    {
        var id = await SeedGoalAsync("Ler mais", RealAiApiFactory.Now);
        await SeedAsync("DetailGoals", DetailingDraft(id));

        var reply = await Send("quero ler 5 livros");

        Assert.Equal(5m, Assert.Single((await StateAsync()).Goals).GetProperty("targetValue").GetDecimal());
        Assert.Contains("5 livros", reply);
        Assert.Null(Assert.Single(await GoalsAsync()).TargetValue);
        Assert.Equal("DetailGoals", (await StateAsync()).Stage);
    }

    [RealAiTheory]
    [InlineData("pular")]
    [InlineData("deixa assim por enquanto")]
    public async Task Skipping_keeps_the_goal_as_it_was(string answer)
    {
        var id = await SeedGoalAsync("Ler mais", RealAiApiFactory.Now);
        await SeedAsync("DetailGoals", DetailingDraft(id));

        var reply = await Send(answer);

        Assert.StartsWith("Tudo bem, deixei **Ler mais** como estava.", reply);
        Assert.Null(Assert.Single(await GoalsAsync()).Indicator);
        Assert.Equal("CollectGoals", (await StateAsync()).Stage);
    }

    // ---------- The path classifier ----------

    [RealAiFact]
    public async Task A_new_goal_while_detailing_goes_to_collecting_it()
    {
        var id = await SeedGoalAsync("Ler mais", RealAiApiFactory.Now);
        await SeedAsync("DetailGoals", DetailingDraft(id));

        var reply = await Send("na verdade quero adicionar outra meta: correr 5 km até o fim do ano");

        Assert.Equal("ConfirmGoals", (await StateAsync()).Stage);
        Assert.Contains("5 km", reply);
        Assert.Single(await GoalsAsync()); // nothing new is saved before the "sim"
    }

    [RealAiFact]
    public async Task Asking_to_talk_about_a_saved_goal_starts_detailing_it()
    {
        await SeedAsync("CollectGoals", PeriodOnly);
        await SeedGoalAsync("Emagrecer até 90 kg", RealAiApiFactory.Now.AddMinutes(-2));
        var books = await SeedGoalAsync("Ler mais", RealAiApiFactory.Now.AddMinutes(-1));

        var reply = await Send("quero detalhar melhor a meta dos livros");

        var state = await StateAsync();
        Assert.Equal("DetailGoals", state.Stage);
        Assert.Equal(books, state.Draft.GetProperty("detailQueue")[0].GetGuid());
        Assert.StartsWith("Claro!", reply);
    }

    // Waiting for "sim" to a proposal for the saved goal "Ler mais".
    private static string DetailingDraft(Guid goalId) =>
        $$"""
        {
          "horizon": "2026-12-31",
          "goals": [{ "name": "Ler 2 livros", "indicator": "livros terminados", "targetValue": 2, "unit": "livros",
                      "dueDate": "2026-12-31" }],
          "doubts": [],
          "detailQueue": ["{{goalId}}"],
          "assumptions": ["meta: 2 livros", "prazo: o fim do período, 31/12/2026"]
        }
        """;

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
        command.Parameters.Add(new() { Value = RealAiApiFactory.Now.AddHours(-1) });
        command.Parameters.Add(new() { Value = (object?)draft ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });
        await command.ExecuteNonQueryAsync();
    }

    // A goal saved at a chosen moment (IGoalService would stamp it with the test clock's "now").
    private async Task<Guid> SeedGoalAsync(string name, DateTimeOffset createdAt)
    {
        var id = Guid.CreateVersion7();
        await using var command = Db.CreateCommand(
            "INSERT INTO goals.goals (id, user_id, name, created_at) VALUES ($1, $2, $3, $4)");
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = _userId });
        command.Parameters.Add(new() { Value = name });
        command.Parameters.Add(new() { Value = createdAt });
        await command.ExecuteNonQueryAsync();
        return id;
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
