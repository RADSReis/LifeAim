using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.Fitness;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Fitness.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>
/// The weight-loss conversation (the first goal specialty): what the user tells through the day is saved in the
/// journal by Pippin himself, and the numbers he reports are the ones the code added up.
/// </summary>
public class FitnessConversationTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 7, 13);

    private readonly MovingTime _time = new(At(Today, 13, 40));
    private readonly InMemoryConversationStore _store = new();
    private readonly FakeGoalService _goals = new();
    private readonly FakeProgressService _progress = new();
    private readonly FakeCheckInService _checkIns;
    private readonly FakeFitnessJournal _journal = new();
    private readonly FakeFitnessReminders _reminders = new();
    private readonly GoalDto _weight;
    private readonly GoalDto _gym;

    public FitnessConversationTests()
    {
        _checkIns = new FakeCheckInService(_time);
        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");
        _weight = _goals.Add(UserId, "Chegar a menos de 84 kg", DateTimeOffset.UtcNow);
        _gym = _goals.Add(UserId, "Academia 3x por semana", DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task The_journal_opens_with_a_goal_about_weight_or_training_and_remembers_which_ones()
    {
        var agent = Agent(new FakeChatClient("not used"));

        Assert.True(await agent.IsActiveAsync(UserId, null, CancellationToken.None));
        Assert.Equal((_weight.Id, _gym.Id), (_journal.Profiles[UserId].WeightGoalId, _journal.Profiles[UserId].ExerciseGoalId));

        // Paused or ended, a goal stops opening it; with none left, the journal is closed.
        await _goals.PauseAsync(_weight.Id, UserId, null, null);
        Assert.True(await agent.IsActiveAsync(UserId, null, CancellationToken.None)); // the gym goal is still there
        await _goals.CancelAsync(_gym.Id, UserId);
        Assert.False(await agent.IsActiveAsync(UserId, null, CancellationToken.None));

        var reader = Guid.CreateVersion7();
        _goals.Add(reader, "Ler 12 livros", DateTimeOffset.UtcNow);
        Assert.False(await agent.IsActiveAsync(reader, null, CancellationToken.None));
    }

    [Fact]
    public async Task Without_such_a_goal_a_meal_gets_the_invitation_to_create_one_and_nothing_is_recorded()
    {
        var reader = Guid.CreateVersion7();
        _store.States[reader] = new ConversationState(Stage.CollectGoals, "Ana");
        _goals.Add(reader, "Ler 12 livros", DateTimeOffset.UtcNow);
        var ai = new FakeChatClient(FakeChatClient.Text("""{ "aboutGoals": false, "aboutFitness": true }"""));

        var reply = await Workflow(ai).ReplyAsync(new PippinMessage(reader, "ana", "almocei arroz e frango"));

        Assert.Equal(PippinWorkflow.FitnessNeedsAGoal, reply);
        Assert.Single(ai.Calls);
        Assert.Empty(_journal.Meals);
    }

    [Theory]
    [InlineData("Emagrecer até 90 kg", null, true, false)]
    [InlineData("Chegar aos 84", "kg", true, false)]
    [InlineData("Baixar o percentual de gordura", null, true, false)]
    [InlineData("Academia 3x por semana", null, false, true)]
    [InlineData("Fazer musculação", null, false, true)]
    [InlineData("Correr 5 km", "km", false, true)]
    [InlineData("Ler 12 livros", "livros", false, false)]
    [InlineData("Acordar mais cedo", null, false, false)]
    public void A_goal_has_the_specialty_by_what_it_is_about(string name, string? unit, bool weight, bool training)
    {
        var goal = new GoalDto(Guid.CreateVersion7(), UserId, name, null, null, null, null, null, unit, DateTimeOffset.UtcNow);

        Assert.Equal((weight, training), (FitnessGoals.IsWeight(goal), FitnessGoals.IsTraining(goal)));
    }

    [Fact]
    public async Task A_meal_told_between_tasks_goes_straight_to_the_journal()
    {
        var ai = new FakeChatClient(
            FakeChatClient.Text("""{ "aboutGoals": false, "aboutFitness": true }"""), // CollectGoals: "this is the journal"
            FakeChatClient.ToolCall("log_meal", new()
            {
                ["name"] = "Almoço",
                ["time"] = "13:30",
                ["items"] = new object[]
                {
                    new Dictionary<string, object?> { ["description"] = "Arroz", ["quantity"] = 100, ["unit"] = "g", ["kcal"] = 128, ["proteinG"] = 2.5, ["source"] = "estimate" },
                    new Dictionary<string, object?> { ["description"] = "Frango cozido", ["quantity"] = 160, ["unit"] = "g", ["kcal"] = 300, ["kcalMax"] = 380, ["proteinG"] = 40, ["proteinMaxG"] = 48, ["source"] = "estimate" },
                },
            }),
            FakeChatClient.Text("Registrado! ✅ **13:30 — Almoço**"));

        var reply = await Workflow(ai).ReplyAsync(Message("almoço às 13:30: 100g de arroz e 250g de frango cozido com osso"));

        Assert.Equal("Registrado! ✅ **13:30 — Almoço**", reply);
        Assert.Equal(3, ai.Calls.Count); // no path classifier in between
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);

        var meal = Assert.Single(_journal.Meals).Meal;
        Assert.Equal((Today, new TimeOnly(13, 30), "Almoço", 428m, 508m), (meal.Day, meal.EatenAt, meal.Name, meal.KcalMin, meal.KcalMax));
        Assert.Equal((42.5m, 50.5m), (meal.ProteinMinG, meal.ProteinMaxG));

        // What the tool told the model: the day as it is now, with the totals added up by the code.
        var toolResult = ToolResults(ai.Calls[^1]).Single();
        Assert.Contains("1. 13:30 — Almoço: ~428–508 kcal · proteína ~42,5–50,5 g", toolResult);
        Assert.Contains("Totais do dia: ingerido ~428–508 kcal", toolResult);
    }

    [Fact]
    public async Task While_the_conversation_is_in_the_journal_the_next_message_skips_the_triage()
    {
        var first = new FakeChatClient(
            FakeChatClient.Text("""{ "aboutGoals": false, "aboutFitness": true }"""),
            FakeChatClient.ToolCall("log_body", new() { ["weightKg"] = 94.5 }),
            FakeChatClient.Text("Anotado: 94,5 kg."));
        await Workflow(first).ReplyAsync(Message("estou com 94,5 kg"));
        Assert.NotNull(_store.States[UserId].Draft?.FitnessTalkAt);

        // Ten minutes later: straight to the fitness agent (2 calls: the tool and the answer).
        _time.Now = _time.Now.AddMinutes(10);
        var second = new FakeChatClient(
            FakeChatClient.ToolCall("add_day_note", new() { ["note"] = "dormiu mal" }),
            FakeChatClient.Text("Anotei."));
        Assert.Equal("Anotei.", await Workflow(second).ReplyAsync(Message("dormi mal hoje")));
        Assert.Equal(2, second.Calls.Count);
        Assert.Contains(second.Calls[0], m => m.Role == ChatRole.System && m.Text.Contains("parceiro de emagrecimento"));

        // Two hours later the conversation is not "in the journal" any more: the usual triage first.
        _time.Now = _time.Now.AddHours(2);
        var third = new FakeChatClient(FakeChatClient.Text("""{ "aboutGoals": false, "isGreeting": true }"""));
        Assert.StartsWith("Opa! Olá, Rafael!", await Workflow(third).ReplyAsync(Message("oi")));
    }

    [Fact]
    public async Task What_is_not_for_the_journal_is_handed_back_to_the_usual_path()
    {
        _store.States[UserId] = new ConversationState(
            Stage.CollectGoals, "Rafael", GoalsDraft.Empty(null) with { FitnessTalkAt = _time.Now.AddMinutes(-5) });

        var ai = new FakeChatClient(
            FakeChatClient.ToolCall("hand_back", new()),
            FakeChatClient.Text(""), // the fitness agent says nothing
            FakeChatClient.Text("""{ "aboutGoals": false, "aboutSavedGoal": true }"""), // CollectGoals: about a saved goal
            FakeChatClient.ToolCall("get_goal", new() { ["goalNumber"] = 1 }),
            FakeChatClient.Text("Sua meta é chegar a menos de 84 kg."));

        var reply = await Workflow(ai).ReplyAsync(Message("qual é mesmo a minha meta de peso?"));

        Assert.Equal("Sua meta é chegar a menos de 84 kg.", reply); // the goals agent, without the path classifier
        Assert.Equal(5, ai.Calls.Count);
        Assert.Null(_store.States[UserId].Draft?.FitnessTalkAt);
        Assert.Empty(_journal.Meals);
    }

    [Fact]
    public async Task The_prompt_carries_the_profile_the_food_table_today_and_the_last_days()
    {
        await _journal.SaveProfileAsync(new SaveProfileRequest(UserId, HeightCm: 178, TargetWeightKg: 84, DailyEnergyKcal: 2350, ProteinTargetG: 150));
        await _journal.SaveFoodAsync(new SaveFoodRequest(UserId, "Whey Growth Chocolate", "30 g (1 scoop)", 118, 23));
        await _journal.RecordBodyAsync(new RecordBodyRequest(UserId, Today, WeightKg: 94.5m));
        await _journal.RecordBodyAsync(new RecordBodyRequest(UserId, Today.AddDays(-1), WeightKg: 94.9m));
        await _journal.AddMealAsync(new AddMealRequest(UserId, Today, new TimeOnly(10, 0), "Café da manhã", [Item("Pão francês", 50, "g", 150, 150, 4, 4)]));
        await _journal.AddMealAsync(new AddMealRequest(UserId, Today.AddDays(-1), new TimeOnly(21, 0), "Jantar", [Item("Pizza", 235, "g", 600, 700, 25, 30)]));
        await _journal.AddDayNoteAsync(UserId, Today.AddDays(-1), "aniversário");

        var ai = new FakeChatClient(FakeChatClient.Text("Sobram ~2.200 kcal."));
        await Agent(ai).ReplyAsync(Context("quanto ainda posso comer hoje?"), CancellationToken.None);

        var prompt = ai.Calls[0][0].Text;
        Assert.Contains("altura 178 cm · peso alvo 84 kg · gasto diário estimado sem treino 2350 kcal · meta de proteína 150 g/dia", prompt);
        Assert.Contains("- Whey Growth Chocolate — 30 g (1 scoop): 118 kcal, 23 g proteína", prompt);
        Assert.Contains("1. 10:00 — Café da manhã: 150 kcal · proteína 4 g", prompt);
        Assert.Contains("Corpo: peso 94,5 kg", prompt);
        Assert.Contains("12/07 dom: peso 94,9 kg · ingerido ~600–700 kcal em 1 refeição(ões)", prompt);
        Assert.Contains("obs.: aniversário", prompt);
        Assert.Contains("1. Chegar a menos de 84 kg", prompt);
        Assert.Contains("Agora são 13:40", prompt);
    }

    [Fact]
    public async Task A_ceia_after_midnight_still_belongs_to_the_day_before()
    {
        _time.Now = At(Today.AddDays(1), 0, 5); // 00:05 of the 14th
        var tools = Tools("ceia agora: 1 barra de 69 kcal");

        var result = await tools.LogMealAsync("Ceia", [new MealItemInput("Barra de proteína", 1, "un", 69, null, 1.3m, null, null, null, "user")], time: "00:02");

        Assert.Equal(Today, Assert.Single(_journal.Meals).Meal.Day);
        Assert.Contains("Dia 13/07/2026", result);
        Assert.Equal(Today, tools.Today);
    }

    [Fact]
    public async Task A_correction_replaces_the_items_of_the_meal_instead_of_adding_another_one()
    {
        var tools = Tools("vou comer 2 torradas e 15g de whey");
        await tools.LogMealAsync("Café da tarde",
        [
            new MealItemInput("Torrada", 2, "un", 70, null, 2, null, null, null, "estimate"),
            new MealItemInput("Whey protein", 15, "g", 59, null, 11.5m, null, null, null, "food_table"),
        ]);

        var corrected = await tools.UpdateMealAsync(1, items:
        [
            new MealItemInput("Torrada", 3, "un", 105, null, 3, null, null, null, "estimate"),
            new MealItemInput("Whey protein", 30, "g", 118, null, 23, null, null, null, "food_table"),
        ]);
        var timed = await tools.SetMealTimeAsync(1, "17:15"); // only the time: the corrected items stay

        var meal = Assert.Single(_journal.Meals).Meal;
        Assert.Equal((new TimeOnly(17, 15), 223m, 26m), (meal.EatenAt, meal.KcalMin, meal.ProteinMinG));
        Assert.Contains("horário não informado — Café da tarde: 223 kcal", corrected);
        await tools.LogMealAsync("Ceia", [new MealItemInput("Iogurte", 1, "un", 60, null, 5, null, null, null, "user")], time: "23:00");
        Assert.Contains("Café da tarde: 223 kcal · proteína 26 g\n", await tools.GetDayAsync("2026-07-13")); // not the last one any more
        Assert.Contains("23:00 — Ceia: 60 kcal · proteína 5 g ← a última registrada", await tools.GetDayAsync("2026-07-13"));
        await tools.DeleteMealAsync(2);
        Assert.Contains("1. 17:15 — Café da tarde: 223 kcal · proteína 26 g", timed);
    }

    [Fact]
    public async Task The_weight_is_saved_in_the_journal_and_counts_as_progress_of_the_weight_goal()
    {
        await _journal.RecordBodyAsync(new RecordBodyRequest(UserId, Today.AddDays(-1), WeightKg: 94.9m));

        var result = await Tools("estou com 94,5 kg").LogBodyAsync(weightKg: 94.5m, waistCm: 110, weightGoalNumber: 1);

        Assert.Equal((94.5m, 110m), (_journal.Bodies[^1].Record.WeightKg, _journal.Bodies[^1].Record.WaistCm));
        var entry = Assert.Single(_progress.Entries);
        Assert.Equal((_weight.Id, Today, 94.5m, "kg", "pesou 94,5 kg"), (entry.GoalId, entry.OccurredOn, entry.Amount, entry.Unit, entry.Note));
        Assert.Contains("Último peso: 94,5 kg em 13/07 · primeiro do período: 94,9 kg em 12/07 (-0,4 kg)", result);
    }

    [Fact]
    public async Task A_typo_in_the_weight_is_refused_and_nothing_is_recorded()
    {
        var result = await Tools("estou com 945 kg").LogBodyAsync(weightKg: 945, weightGoalNumber: 1);

        Assert.StartsWith("Erro:", result);
        Assert.Empty(_journal.Bodies);
        Assert.Empty(_progress.Entries);
    }

    [Fact]
    public async Task A_workout_told_in_the_follow_up_is_saved_in_the_journal_and_answers_the_follow_up()
    {
        var routine = Guid.CreateVersion7();
        var checkIn = _checkIns.Add(UserId, _gym.Id, routine, Today, "ir à academia", _time.Now);
        _store.States[UserId] = new ConversationState(
            Stage.CheckIn,
            "Rafael",
            GoalsDraft.Empty(null) with { CheckIns = [new CheckInItem(checkIn.Id, _gym.Id, _gym.Name, "ir à academia", Today, null, null)] });

        var ai = new FakeChatClient(
            FakeChatClient.Text("""{ "decision": "fitness", "items": [], "reply": null }"""), // the follow-up: "this is the journal"
            FakeChatClient.ToolCall("log_exercise", new()
            {
                ["activity"] = "Elíptico", ["durationMinutes"] = 30, ["kcal"] = 332, ["measured"] = true, ["time"] = "20:30", ["goalNumber"] = 2,
            }),
            FakeChatClient.Text("Treino registrado! 💪"));

        var reply = await Workflow(ai).ReplyAsync(Message("fiz 30 min de elíptico às 20:30, 332 kcal no relógio"));

        Assert.Equal("Treino registrado! 💪", reply); // the follow-up's question is not repeated after it
        var exercise = Assert.Single(_journal.Exercises).Exercise;
        Assert.Equal(("Elíptico", 30, 332m, 332m, true), (exercise.Activity, exercise.DurationMinutes, exercise.KcalMin, exercise.KcalMax, exercise.Measured));
        Assert.Equal(_gym.Id, Assert.Single(_progress.Entries).GoalId);
        Assert.Equal(CheckInStatus.Done, _checkIns.CheckIns.Single().Status);
    }

    [Fact]
    public async Task The_path_classifier_also_leads_to_the_journal_and_a_cadastro_in_progress_gets_its_question_back()
    {
        var draft = new GoalsDraft(new DateOnly(2026, 12, 31), [new GoalDraft("Ler 12 livros", null, null, null, null, null, null)], []);
        _store.States[UserId] = new ConversationState(Stage.ConfirmGoals, "Rafael", draft);
        var planning = new FakePlanningService();
        var detailing = new GoalDetailing(new FakeChatClient("not used"), _goals, planning, _time);

        var ai = new FakeChatClient(
            FakeChatClient.Text("""{ "decision": "other" }"""), // ConfirmGoals: not an answer to the confirmation
            FakeChatClient.Text("""{ "route": "fitness_talk", "reason": "peso do dia" }"""),
            FakeChatClient.ToolCall("log_body", new() { ["weightKg"] = 94.5 }),
            FakeChatClient.Text("Anotado: 94,5 kg."));

        var reply = await Workflow(ai, new ConfirmGoalsNode(ai, _goals, detailing, _time)).ReplyAsync(Message("estou com 94,5 kg hoje"));

        Assert.StartsWith("Anotado: 94,5 kg.\n\n" + FallbackRouter.BackToQuestion, reply);
        Assert.Equal(94.5m, Assert.Single(_journal.Bodies).Record.WeightKg);
        Assert.Equal(Stage.ConfirmGoals, _store.States[UserId].Stage);
        Assert.Equal(_weight.Id, Assert.Single(_progress.Entries).GoalId); // no goal said: the journal knows its weight goal
    }

    [Fact]
    public async Task The_pending_question_is_not_repeated_after_every_answer_of_the_journal()
    {
        var draft = new GoalsDraft(new DateOnly(2026, 12, 31), [new GoalDraft("Ler 12 livros", null, null, null, null, null, null)], []);
        _store.States[UserId] = new ConversationState(Stage.ConfirmGoals, "Rafael", draft);
        var detailing = new GoalDetailing(new FakeChatClient("not used"), _goals, new FakePlanningService(), _time);
        FakeChatClient Ai() => new(
            FakeChatClient.Text("""{ "decision": "other" }"""),
            FakeChatClient.Text("""{ "route": "fitness_talk" }"""),
            FakeChatClient.ToolCall("add_day_note", new() { ["note"] = "dormiu mal" }),
            FakeChatClient.Text("Anotei."));

        var (first, second) = (Ai(), Ai());
        var one = await Workflow(first, new ConfirmGoalsNode(first, _goals, detailing, _time)).ReplyAsync(Message("dormi mal"));
        var two = await Workflow(second, new ConfirmGoalsNode(second, _goals, detailing, _time)).ReplyAsync(Message("e acordei cedo"));

        Assert.Contains(FallbackRouter.BackToQuestion, one);
        Assert.Equal("Anotei.", two);
    }

    [Fact]
    public async Task Someone_who_keeps_the_journal_is_greeted_with_the_day_so_far()
    {
        await _journal.RecordBodyAsync(new RecordBodyRequest(UserId, Today, WeightKg: 94.5m));
        await _journal.AddMealAsync(new AddMealRequest(UserId, Today, new TimeOnly(10, 0), "Café da manhã", [Item("Pão", 50, "g", 150, 150, 4, 4)]));

        var reply = await Workflow(new FakeChatClient("""{ "aboutGoals": false, "isGreeting": true }""")).ReplyAsync(Message("bom dia"));

        Assert.StartsWith("Opa! Olá, Rafael! 😊\n\nHoje no diário: peso 94,5 kg · 1 refeição(ões), 150 kcal · proteína 4 g.", reply);
        Assert.DoesNotContain("**Suas metas:**", reply);
    }

    [Fact]
    public async Task The_journal_remembers_which_goal_a_weigh_in_and_a_workout_belong_to()
    {
        var tools = Tools("treinei");
        await tools.LogExerciseAsync("Musculação", 30, goalNumber: 2);
        await tools.LogBodyAsync(weightKg: 94.5m, weightGoalNumber: 1);

        // The next day nobody says the goal: the journal already knows, so the follow-up of each goal is answered.
        _time.Now = _time.Now.AddDays(1);
        await tools.LogExerciseAsync("Elíptico", 20);
        await tools.LogBodyAsync(weightKg: 94.2m);

        Assert.Equal((_weight.Id, _gym.Id), (_journal.Profiles[UserId].WeightGoalId, _journal.Profiles[UserId].ExerciseGoalId));
        Assert.Equal([_gym.Id, _weight.Id, _gym.Id, _weight.Id], _progress.Entries.Select(e => e.GoalId));

        // A goal that is not active any more is not credited.
        await _goals.CancelAsync(_gym.Id, UserId);
        await tools.LogExerciseAsync("Esteira", 20);
        Assert.Equal(4, _progress.Entries.Count);
    }

    [Fact]
    public async Task A_photo_goes_straight_to_the_fitness_agent_for_this_message_only_and_is_not_kept()
    {
        var ai = new FakeChatClient(
            FakeChatClient.ToolCall("save_food", new() { ["name"] = "Barra Exemplo", ["serving"] = "1 barra de 30 g", ["kcal"] = 120, ["proteinG"] = 10 }),
            FakeChatClient.Text("Guardei o rótulo da **Barra Exemplo**."));
        byte[] photo = [0x89, 0x50, 0x4E, 0x47];

        var reply = await Workflow(ai).ReplyAsync(new PippinMessage(UserId, "rafael", "", [
            new PippinImage(photo, "image/png"),
            new PippinImage(new byte[10], "application/pdf"), // not a photo: ignored
        ]));

        Assert.Equal("Guardei o rótulo da **Barra Exemplo**.", reply);
        Assert.Equal(2, ai.Calls.Count); // no triage: nothing else could look at a photo
        var sent = ai.Calls[0].Last(m => m.Role == ChatRole.User);
        Assert.Equal(photo, Assert.Single(sent.Contents.OfType<DataContent>()).Data.ToArray());
        Assert.Equal("(só a foto, sem texto)", sent.Text);
        Assert.Equal(120, Assert.Single(_journal.Foods).Food.Kcal);

        // The history keeps the words and the fact that there was a photo, not the photo.
        Assert.Equal("[enviou 1 foto(s)]", _store.Messages.First(m => m.Turn.Role == ConversationRole.User).Turn.Content);
    }

    [Fact]
    public async Task The_day_shows_what_is_left_and_the_balance_when_the_profile_has_the_numbers()
    {
        await _journal.SaveProfileAsync(new SaveProfileRequest(UserId, DailyEnergyKcal: 2350, CalorieTargetKcal: 1900));
        await _journal.AddMealAsync(new AddMealRequest(UserId, Today, new TimeOnly(10, 0), "Café da manhã", [Item("Pão com manteiga e whey", null, null, 510, 540, 30, 30)]));
        await _journal.AddExerciseAsync(new AddExerciseRequest(UserId, Today, null, "Bicicleta", 20, 140, 200, false));

        var day = await Tools("como está meu dia?").GetDayAsync("2026-07-13");

        Assert.Contains("Totais do dia: ingerido ~510–540 kcal · proteína 30 g · gasto com exercício ~140–200 kcal", day);
        Assert.Contains("Meta de calorias do dia: 1.900 kcal → restam ~1.360–1.390 kcal", day);
        Assert.Contains("Gasto estimado do dia: 2.350 kcal sem treino + exercício = ~2.490–2.550 kcal → se o dia terminasse agora: déficit de ~1.950–2.040 kcal", day);
    }

    [Fact]
    public async Task The_history_gives_one_line_per_day_the_averages_and_the_weight_trend()
    {
        for (var i = 1; i <= 3; i++)
        {
            var day = Today.AddDays(-i);
            await _journal.RecordBodyAsync(new RecordBodyRequest(UserId, day, WeightKg: 94m + i * 0.2m));
            await _journal.AddMealAsync(new AddMealRequest(UserId, day, new TimeOnly(13, 0), "Almoço", [Item("Prato", null, null, 1800 + i * 100, 2000 + i * 100, 130, 140)]));
        }

        await _journal.AddExerciseAsync(new AddExerciseRequest(UserId, Today.AddDays(-2), null, "Musculação", 30, 225, null, true));

        var history = await Tools("como foi minha semana?").GetHistoryAsync("2026-07-06");

        Assert.Contains("Diário de 06/07/2026 a 13/07/2026 (3 dia(s) com registro):", history);
        Assert.Contains("11/07 sáb: peso 94,4 kg · ingerido ~2.000–2.200 kcal em 1 refeição(ões) · proteína ~130–140 g · treino: Musculação 30 min (225 kcal)", history);
        Assert.Contains("Média dos 3 dias com refeições registradas: ingerido ~2.000–2.200 kcal · proteína ~130–140 g", history);
        Assert.Contains("Dias com treino: 1.", history);
        Assert.Contains("Último peso: 94,2 kg em 12/07 · primeiro do período: 94,6 kg em 10/07 (-0,4 kg)", history);
    }

    [Fact]
    public async Task Nothing_is_recorded_in_the_future_or_on_a_meal_that_does_not_exist()
    {
        var tools = Tools("amanhã vou comer pizza");

        Assert.StartsWith("Erro:", await tools.LogMealAsync("Jantar", [new MealItemInput("Pizza", 2, "fatias", 500, 600, 20, 25, null, null, "estimate")], date: "2026-07-20"));
        Assert.StartsWith("Erro:", await tools.DeleteMealAsync(1));
        Assert.StartsWith("Erro:", await tools.UpdateMealAsync(3, name: "Ceia"));
        Assert.StartsWith("Erro:", await tools.SetMealTimeAsync(1, "às cinco"));
        Assert.StartsWith("Erro:", await tools.LogMealAsync("Jantar", [], time: "25:99"));
        Assert.Empty(_journal.Meals);
    }

    [Fact]
    public async Task A_common_food_comes_from_the_reference_table_in_proportion_to_the_amount()
    {
        _journal.Reference.Add(new ReferenceFoodDto("Arroz, tipo 1, cozido", "Cereais e derivados", 128, 2.5m, 28.1m, 0.2m, 1.6m));
        var tools = Tools("almoço: 80g de arroz");

        var found = await tools.SearchFoodAsync("arroz cozido");
        Assert.Contains("- Arroz, tipo 1, cozido — por 100 g: 128 kcal, 2,5 g proteína, 28,1 g carboidratos, 0,2 g gorduras, 1,6 g fibra", found);
        Assert.StartsWith("Nada parecido", await tools.SearchFoodAsync("poke de salmão"));

        // Only the row's name and the grams: the code reads the table and does the maths.
        var logged = await tools.LogMealAsync("Almoço",
        [
            new MealItemInput("Arroz branco", 80, "g", 0, null, 0, null, null, null, "reference_table", ReferenceName: "arroz, tipo 1, cozido"),
            new MealItemInput("Molho da casa", 30, "g", 40, 60, 0, 1, null, null, "reference_table"), // no row named: an estimate
        ]);

        var items = Assert.Single(_journal.Meals).Meal.Items;
        Assert.Equal((102.4m, 102.4m, 2m, 22.5m, 0.2m), (items[0].KcalMin, items[0].KcalMax, items[0].ProteinMinG, items[0].CarbsG, items[0].FatG));
        Assert.Equal(NutritionSource.Estimate, items[1].Source);

        Assert.Equal(NutritionSource.ReferenceTable, items[0].Source);
        Assert.Contains("Arroz branco 80 g: 102 kcal, proteína 2 g, carboidratos 22,5 g, gorduras 0,2 g (TACO)", logged);
    }

    [Fact]
    public void The_tools_tell_the_model_what_each_argument_is()
    {
        var tools = Tools("oi").All().OfType<AIFunction>().ToDictionary(t => t.Name);

        Assert.Equal(
            ["add_day_note", "complete_reminder", "delete_exercise", "delete_meal", "get_day", "get_history", "log_body", "log_exercise", "log_meal", "remove_reminder",
                "save_food", "search_food", "set_meal_time", "set_reminder", "update_meal", "update_profile"],
            tools.Keys.Order());

        var logMeal = tools["log_meal"].JsonSchema.GetRawText();
        Assert.Contains("\"kcalMax\"", logMeal);
        Assert.Contains("ximo da estimativa de kcal", logMeal); // the items' fields are described too
        Assert.DoesNotContain("cancellationToken", logMeal);
    }

    private static MealItemDto Item(string description, decimal? quantity, string? unit, decimal kcalMin, decimal kcalMax, decimal proteinMin, decimal proteinMax) =>
        new(description, quantity, unit, kcalMin, kcalMax, proteinMin, proteinMax, null, null, NutritionSource.Estimate);

    private FitnessTools Tools(string message) => new(Context(message), _journal, _reminders, _goals, _progress, _checkIns, _time);

    private FitnessAgent Agent(IChatClient ai) => new(ai, _journal, _reminders, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance);

    private ConversationContext Context(string message) => new()
    {
        UserId = UserId,
        DisplayName = "rafael",
        IncomingText = message,
        History = [],
        Stage = Stage.CollectGoals,
        UserName = "Rafael",
        SavedGoals = _goals.Saved,
    };

    private PippinWorkflow Workflow(IChatClient ai, params INode[] extraNodes) =>
        new(
            _store,
            [new GreetingNode(), new CollectGoalsNode(ai, _time), new CheckInNode(ai, _progress, _checkIns, _time), .. extraNodes],
            new FallbackRouter(),
            new GreetingReply(),
            new PathClassifier(ai),
            new GoalsAgent(ai, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance),
            Agent(ai),
            _goals,
            new FakePippinChannel(),
            NullLogger<PippinWorkflow>.Instance);

    private static IEnumerable<string> ToolResults(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? "");

    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(-3));

    private static PippinMessage Message(string text) => new(UserId, "rafael", text);

    private sealed class MovingTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
