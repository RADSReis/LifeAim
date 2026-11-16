using System.Text.Json;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
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
        builder.ConfigureTestServices(services =>
        {
            LifeAimApiFactory.WithoutBackgroundJobs(services);
            services.AddSingleton<TimeProvider>(new FixedClock(Now));
        });
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

        // Straight to deepening, one goal at a time: one question, then the plan for confirmation.
        Assert.Equal("DetailGoals", (await StateAsync()).Stage);
        Assert.Empty(await RoutinesAsync());

        var firstPlan = await Send("hoje continuo com 94 kg, vou na academia 3 vezes por semana e leio 10 páginas toda noite");
        Assert.Contains("Está certo?", firstPlan);
        await Send("sim");

        var secondPlan = await Send("pode propor um plano");
        Assert.Contains("Está certo?", secondPlan);
        var finalSummary = await Send("sim");

        Assert.Contains("Esse é o resumo das metas.", finalSummary);
        Assert.Equal("CollectGoals", (await StateAsync()).Stage);
        Assert.NotEmpty(await RoutinesAsync());
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
            "Veio me trazer novidades sobre suas metas? Sobre qual delas vamos falar hoje? " +
            "Se quiser, também dá para cadastrar uma meta nova.",
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

    // ---------- ⑤ Deepening: one question, then the plan ----------

    [RealAiFact]
    public async Task Saving_goes_straight_to_one_question_about_the_first_goal()
    {
        await SeedAsync("ConfirmGoals",
            $$"""{ "horizon": "2026-12-31", "goals": [{{WeightGoal}}], "doubts": [] }""");

        var reply = await Send("sim");

        var state = await StateAsync();
        Assert.Equal("DetailGoals", state.Stage);
        Assert.Contains("propor um plano", reply); // the question offers a proposal
        Assert.Contains("pesa", reply); // a weight goal asks for today's weight
    }

    [RealAiFact]
    public async Task The_plan_becomes_routines_and_sim_saves_them_with_the_users_words()
    {
        var id = await SeedGoalAsync("Ler 2 livros", RealAiApiFactory.Now);
        await SeedAsync("DetailGoals", DetailingDraft(id));
        const string plan = "vou ler segunda, quarta e sexta de manhã, umas 10 páginas";

        var shown = await Send(plan);

        Assert.Contains("segunda, quarta e sexta", shown);
        Assert.Contains("de manhã", shown);
        Assert.Equal("DetailGoals", (await StateAsync()).Stage);
        Assert.Empty(await RoutinesAsync()); // nothing saved before the confirmation

        await Send("sim");

        var routine = Assert.Single(await RoutinesAsync());
        Assert.Equal([1, 3, 5], routine.DaysOfWeek);
        Assert.Equal((DayPeriod.Morning, 10m, RoutineSource.User), (routine.DayPeriod!.Value, routine.Amount!.Value, routine.Source));
        Assert.Equal(plan, Assert.Single(await GoalsAsync()).UserPlan);
    }

    [RealAiFact]
    public async Task Todays_weight_goes_to_the_start_value()
    {
        var id = await SeedGoalAsync("Emagrecer até 90 kg", RealAiApiFactory.Now);
        await SeedAsync("DetailGoals", DetailingDraft(id, WeightGoal));

        var shown = await Send("hoje peso 97 kg e vou na academia 3 vezes por semana");

        var goal = Assert.Single((await StateAsync()).Goals);
        Assert.Equal(97m, goal.GetProperty("startValue").GetDecimal());
        Assert.Contains("97 kg", shown);
        Assert.Contains("3x por semana", shown);
    }

    [RealAiFact]
    public async Task Asking_for_a_proposal_shows_pippins_plan()
    {
        var id = await SeedGoalAsync("Ler 2 livros", RealAiApiFactory.Now);
        await SeedAsync("DetailGoals", DetailingDraft(id));

        var shown = await Send("não sei, pode propor um plano pra mim");

        Assert.StartsWith("Minha proposta de plano para", shown);
        var plan = (await StateAsync()).Draft.GetProperty("plan");
        Assert.Equal("pippin", plan.GetProperty("source").GetString());
        Assert.NotEqual(0, plan.GetProperty("routines").GetArrayLength());
    }

    [RealAiFact]
    public async Task Ignoring_the_confirmation_saves_the_plan_and_handles_the_new_message()
    {
        var id = await SeedGoalAsync("Ler 2 livros", RealAiApiFactory.Now);
        await SeedAsync("DetailGoals", DetailingDraft(id, withPlan: true));

        var reply = await Send("quero adicionar outra meta: correr 5 km até o fim do ano");

        Assert.StartsWith("Salvei o plano de **Ler 2 livros**! ✅", reply);
        Assert.Single(await RoutinesAsync());
        Assert.Equal("ConfirmGoals", (await StateAsync()).Stage);
        Assert.Contains("5 km", reply);
    }

    [RealAiTheory]
    [InlineData("pula essa")]
    [InlineData("depois eu vejo isso")]
    public async Task Skipping_leaves_the_plan_for_later(string answer)
    {
        var id = await SeedGoalAsync("Ler 2 livros", RealAiApiFactory.Now);
        await SeedAsync("DetailGoals", DetailingDraft(id));

        var reply = await Send(answer);

        Assert.StartsWith("Tudo bem! Quando quiser, é só me contar o plano de **Ler 2 livros**.", reply);
        Assert.Empty(await RoutinesAsync());
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

    // ---------- The goals agent (tools + loop) with the real model ----------

    // Two saved goals with their values, between tasks (CollectGoals): where most of these talks will happen.
    private async Task<(Guid Weight, Guid Books)> SeedTwoPlannedGoalsAsync()
    {
        await SeedAsync("CollectGoals", PeriodOnly);
        var weight = await SeedGoalAsync("Emagrecer até 90 kg", RealAiApiFactory.Now.AddMinutes(-2));
        var books = await SeedGoalAsync("Ler 2 livros", RealAiApiFactory.Now.AddMinutes(-1));
        var goals = factory.Services.GetRequiredService<IGoalService>();
        await goals.UpdateAsync(new UpdateGoalRequest(
            weight, _userId, "Emagrecer até 90 kg", null, "peso corporal", new DateOnly(2026, 12, 25), 94m, 90m, "kg", null));
        await goals.UpdateAsync(new UpdateGoalRequest(
            books, _userId, "Ler 2 livros", null, "livros terminados", new DateOnly(2026, 12, 31), null, 2m, "livros", null));
        return (weight, books);
    }

    [RealAiFact]
    public async Task Reporting_progress_records_it_for_the_right_goal_even_in_another_unit()
    {
        var (_, books) = await SeedTwoPlannedGoalsAsync();

        await Send("hoje li 12 páginas");

        var entry = Assert.Single(await Progress.ListByGoalAsync(_userId, books));
        Assert.Equal(12m, entry.Amount);
        Assert.Contains("pág", entry.Unit);
        Assert.Equal(new DateOnly(2026, 11, 14), entry.OccurredOn);
        Assert.Equal("CollectGoals", (await StateAsync()).Stage);
    }

    [RealAiFact]
    public async Task Progress_of_yesterday_gets_yesterdays_date()
    {
        var (weight, _) = await SeedTwoPlannedGoalsAsync();

        await Send("ontem me pesei e deu 93 kg");

        var entry = Assert.Single(await Progress.ListByGoalAsync(_userId, weight));
        Assert.Equal((93m, new DateOnly(2026, 11, 13)), (entry.Amount!.Value, entry.OccurredOn));
    }

    [RealAiFact]
    public async Task Consulting_a_goal_shows_its_information()
    {
        await SeedTwoPlannedGoalsAsync();

        var reply = await Send("me mostra a meta do peso");

        Assert.Contains("90", reply);
        Assert.Contains("94", reply);
        Assert.Contains("25/12", reply);
    }

    [RealAiFact]
    public async Task Changing_the_due_date_by_conversation_updates_the_goal()
    {
        var (weight, _) = await SeedTwoPlannedGoalsAsync();

        await Send("muda o prazo da meta de peso para 31 de março de 2027");

        var goal = (await GoalsAsync()).Single(g => g.Id == weight);
        Assert.Equal(new DateOnly(2027, 3, 31), goal.DueDate);
        Assert.Equal(90m, goal.TargetValue); // the rest stays
    }

    [RealAiFact]
    public async Task Cancelling_asks_for_confirmation_and_only_sim_cancels()
    {
        var (weight, books) = await SeedTwoPlannedGoalsAsync();

        var question = await Send("a meta sobre leitura de livros tem que ser cancelada");

        Assert.Contains("Tem certeza que a meta **Ler 2 livros** deve ser cancelada?", question);
        Assert.Equal("ConfirmCancelGoal", (await StateAsync()).Stage);
        Assert.Equal(2, (await GoalsAsync()).Count); // nothing cancelled before the "sim"

        await Send("sim");

        Assert.Equal([weight], (await GoalsAsync()).Select(g => g.Id));
        Assert.Equal("CollectGoals", (await StateAsync()).Stage);
        Assert.DoesNotContain(books, (await GoalsAsync()).Select(g => g.Id));
    }

    private IProgressService Progress => factory.Services.GetRequiredService<IProgressService>();

    private const string ReadingGoal =
        """{ "name": "Ler 2 livros", "indicator": "livros terminados", "targetValue": 2, "unit": "livros", "dueDate": "2026-12-31" }""";

    private const string WeightGoal =
        """{ "name": "Emagrecer até 90 kg", "indicator": "peso corporal", "targetValue": 90, "unit": "kg", "dueDate": "2026-12-25" }""";

    // Deepening a saved goal: waiting for the answer to the one question or, with a plan, for "sim" to it.
    private static string DetailingDraft(Guid goalId, string goal = ReadingGoal, bool withPlan = false) =>
        $$"""
        {
          "horizon": "2026-12-31",
          "goals": [{{goal}}],
          "doubts": [],
          "detailQueue": ["{{goalId}}"],
          "question": "Para conseguir **Ler 2 livros**, qual o seu plano? Me diga em poucas palavras, ou se quiser eu posso propor um plano para você.",
          "plan": {{(withPlan ? MorningPlan : "null")}}
        }
        """;

    private const string MorningPlan =
        """
        { "source": "user", "routines": [{ "activity": "ler", "frequency": "weekly", "source": "user", "interval": 1,
          "daysOfWeek": [1, 3, 5], "dayPeriod": "morning", "amount": 10, "unit": "páginas" }] }
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

    private async Task<IReadOnlyList<RoutineDto>> RoutinesAsync() =>
        await factory.Services.GetRequiredService<IGoalService>().ListRoutinesAsync(_userId);

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
        // One command per statement: PostgreSQL does not take several statements with $1 parameters in one command.
        foreach (var sql in new[]
                 {
                     "DELETE FROM goals.goals WHERE user_id = $1",
                     "DELETE FROM progress.progress_entries WHERE user_id = $1",
                     "DELETE FROM planning.goal_proposals WHERE proposal_id IN (SELECT id FROM planning.proposals WHERE user_id = $1)",
                     "DELETE FROM planning.proposals WHERE user_id = $1",
                 })
        {
            await using var command = Db.CreateCommand(sql);
            command.Parameters.Add(new() { Value = _userId });
            await command.ExecuteNonQueryAsync();
        }
    }
}
