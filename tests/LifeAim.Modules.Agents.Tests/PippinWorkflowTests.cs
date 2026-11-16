using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

public class PippinWorkflowTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    // 13/11/2026: between March and November, so Pippin asks "fim deste ano ou mês específico".
    private static readonly FixedTimeProvider November = new(new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero));

    private readonly InMemoryConversationStore _store = new();

    [Fact]
    public async Task First_contact_introduces_pippin_and_moves_to_asking_the_name()
    {
        var workflow = CreateWorkflow(new FakeChatClient("unused"));

        var reply = await workflow.ReplyAsync(Message("oi"));

        Assert.Equal(GreetingNode.Question, reply);
        Assert.Equal(Stage.AskName, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task A_name_is_kept_and_pippin_asks_about_the_goals_horizon()
    {
        var ai = new FakeChatClient("""{ "answeredTheQuestion": true, "name": "Rafa" }""");
        var workflow = CreateWorkflow(ai);
        await workflow.ReplyAsync(Message("oi"));

        var reply = await workflow.ReplyAsync(Message("pode me chamar de Rafa"));

        Assert.StartsWith("Prazer, Rafa!", reply);
        Assert.Contains("fim deste ano ou para algum mês específico", reply);
        Assert.Equal(new ConversationState(Stage.CollectGoals, "Rafa"), _store.States[UserId]);

        // The node sent the AI its instructions and only the user's message.
        var call = Assert.Single(ai.Calls);
        Assert.Equal(ChatRole.System, call[0].Role);
        Assert.Equal("pode me chamar de Rafa", call[^1].Text);
    }

    [Fact]
    public async Task An_unexpected_message_goes_to_the_router_and_the_stage_stays()
    {
        var workflow = CreateWorkflow(new FakeChatClient("""{ "answeredTheQuestion": false, "name": null }"""));
        await workflow.ReplyAsync(Message("oi"));

        var reply = await workflow.ReplyAsync(Message("me conta uma piada"));

        Assert.EndsWith(FallbackRouter.BackToQuestion + AskNameNode.Question, reply);
        Assert.Equal(Stage.AskName, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Unexpected_messages_in_a_row_repeat_the_stage_question_not_the_previous_reply()
    {
        var workflow = CreateWorkflow(new FakeChatClient("""{ "answeredTheQuestion": false, "name": null }"""));
        await workflow.ReplyAsync(Message("oi"));
        await workflow.ReplyAsync(Message("Oi"));

        var reply = await workflow.ReplyAsync(Message("olá"));

        // Before the fix, each reply quoted the previous one: "voltando ao que eu tinha perguntado: … voltando…".
        Assert.Single(Occurrences(reply, FallbackRouter.BackToQuestion));
        Assert.EndsWith(FallbackRouter.BackToQuestion + AskNameNode.Question, reply);
    }

    [Fact]
    public async Task After_the_no_brain_reply_the_router_goes_back_to_the_question_not_to_that_warning()
    {
        var store = _store;
        var withoutKey = CreateWorkflow(new MissingApiKeyChatClient());
        await withoutKey.ReplyAsync(Message("oi"));
        await withoutKey.ReplyAsync(Message("Rafael")); // "sem o meu cérebro de IA"

        // The key is configured and the API restarted: same saved conversation, now with an AI.
        var withKey = CreateWorkflow(new FakeChatClient("""{ "answeredTheQuestion": false, "name": null }"""));
        var reply = await withKey.ReplyAsync(Message("Oi"));

        Assert.DoesNotContain("cérebro", reply);
        Assert.EndsWith(FallbackRouter.BackToQuestion + AskNameNode.Question, reply);
        Assert.Equal(Stage.AskName, store.States[UserId].Stage);
    }

    [Fact]
    public async Task Without_an_api_key_pippin_says_its_brain_is_off_and_waits_at_the_same_stage()
    {
        var workflow = CreateWorkflow(new MissingApiKeyChatClient());
        await workflow.ReplyAsync(Message("oi")); // the greeting needs no AI

        var reply = await workflow.ReplyAsync(Message("Rafael"));

        Assert.Contains("sem o meu cérebro de IA", reply);
        Assert.Equal(Stage.AskName, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Every_turn_is_saved_with_the_stage_it_happened_at()
    {
        var workflow = CreateWorkflow(new FakeChatClient("unused"));

        await workflow.ReplyAsync(Message("oi"));

        Assert.Equal(
            [(ConversationRole.User, "oi", Stage.Greeting), (ConversationRole.Assistant, GreetingNode.Question, Stage.Greeting)],
            _store.Messages.Select(m => (m.Turn.Role, m.Turn.Content, m.Stage)));
    }

    [Theory]
    [InlineData(1, "ao longo deste ano")]
    [InlineData(2, "ao longo deste ano")]
    [InlineData(6, "fim deste ano ou para algum mês específico")]
    [InlineData(12, "metas de ano novo")]
    public void The_goals_horizon_follows_the_month(int month, string expected)
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, month, 15, 15, 0, 0, TimeSpan.Zero));

        Assert.Contains(expected, GoalHorizon.Question(time));
    }

    [Fact]
    public async Task The_name_is_capitalized_by_code_keeping_the_connectors_lowercase()
    {
        var workflow = CreateWorkflow(new FakeChatClient("""{ "answeredTheQuestion": true, "name": "rafael diniz dos santos" }"""));
        await workflow.ReplyAsync(Message("oi"));

        var reply = await workflow.ReplyAsync(Message("rafael diniz dos santos"));

        Assert.StartsWith("Prazer, Rafael Diniz dos Santos!", reply);
        Assert.Equal("Rafael Diniz dos Santos", _store.States[UserId].UserName);
    }

    // ---------- Greetings: a greeting asks for nothing, so it never goes to the router ----------

    private const string JustAGreeting =
        """{ "aboutGoals": false, "isGreeting": true, "horizonDate": null, "goals": [], "doubts": [], "finished": false }""";

    [Fact]
    public async Task A_greeting_while_waiting_for_the_name_greets_back_and_asks_it_again()
    {
        var workflow = CreateWorkflow(new FakeChatClient("""{ "answeredTheQuestion": false, "name": null, "isGreeting": true }"""));
        await workflow.ReplyAsync(Message("oi"));

        var reply = await workflow.ReplyAsync(Message("olá"));

        Assert.Equal("Oi! 😊 Como você se chama?", reply);
        Assert.Equal(Stage.AskName, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task A_greeting_lists_the_saved_goals_and_invites_to_talk_about_one()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var goals = new FakeGoalService();
        goals.Add(UserId, "Emagrecer até 90 kg", November.GetUtcNow().AddDays(-1));
        goals.Add(UserId, "Ler 2 livros", November.GetUtcNow().AddDays(-1));
        var workflow = CreateWorkflow(new FakeChatClient(JustAGreeting), goals);

        var reply = await workflow.ReplyAsync(Message("olá"));

        // UX item 11: greet back, list the goals and ask which one to talk about (the goals agent answers that).
        Assert.Equal(
            "Opa! Olá, Rafael! 😊\n\n**Suas metas:**\n- Emagrecer até 90 kg\n- Ler 2 livros\n\n" +
            GreetingReply.GoalsInvitation,
            reply);
        Assert.DoesNotContain(FallbackRouter.BackToQuestion, reply);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task A_greeting_lists_at_most_10_goals_and_marks_that_there_are_more()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var goals = new FakeGoalService();
        for (var i = 1; i <= 12; i++)
        {
            goals.Add(UserId, $"Meta {i}", November.GetUtcNow().AddMinutes(i));
        }

        var reply = await CreateWorkflow(new FakeChatClient(JustAGreeting), goals).ReplyAsync(Message("oi"));

        Assert.Contains("- Meta 10\n- ...\n\n", reply);
        Assert.DoesNotContain("Meta 11", reply);
    }

    [Fact]
    public async Task A_greeting_without_saved_goals_just_greets_and_asks_for_them()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));

        var reply = await CreateWorkflow(new FakeChatClient(JustAGreeting)).ReplyAsync(Message("bom dia"));

        Assert.Equal("Opa! Olá, Rafael! 😊 Quais metas você quer alcançar até 31/12/2026?", reply);
    }

    [Fact]
    public async Task A_greeting_while_confirming_resumes_the_goals_waiting_for_confirmation()
    {
        AtStage(Stage.ConfirmGoals, ShownDraft());
        var goals = new FakeGoalService();
        goals.Add(UserId, "Correr 5 km", November.GetUtcNow().AddDays(-3)); // saved earlier: not listed here
        var workflow = CreateWorkflow(new FakeChatClient(Confirmation("greeting", booksTarget: 2, keepDoubt: true)), goals);

        var reply = await workflow.ReplyAsync(Message("oi"));

        Assert.StartsWith("Opa! Olá, Rafael! 😊 Voltando de onde paramos:\n\nEntendi estas 2 metas", reply);
        Assert.Contains("**A)** 3 livros", reply);
        Assert.DoesNotContain("Suas metas", reply);
        Assert.Equal(Stage.ConfirmGoals, _store.States[UserId].Stage);
        Assert.Empty(goals.Created);
    }

    [Fact]
    public async Task A_real_request_still_goes_to_the_router_with_the_question_about_more_goals()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var goals = new FakeGoalService();
        goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "aboutGoals": false, "isGreeting": false, "horizonDate": null, "goals": [], "doubts": [], "finished": false }"""), goals);

        var reply = await workflow.ReplyAsync(Message("qual a previsão do tempo?"));

        Assert.EndsWith(FallbackRouter.BackToQuestion + "Quer adicionar mais alguma meta até 31/12/2026?", reply);
    }

    // ---------- ③ Collecting goals ----------

    // What the AI returns for: "vamos definir objetivos até o fim desse ano, estou com 94 kg e quero emagrecer
    // até 90kg até o natal, também estou no fim de 1 livro, mas quero ler mais 2 livros até o fim do ano".
    private const string TwoGoalsInOneSentence =
        """
        {
          "aboutGoals": true,
          "horizonDate": "2026-12-31",
          "goals": [
            { "name": "Emagrecer até 90 kg", "description": null, "indicator": "peso corporal",
              "startValue": 94, "targetValue": 90, "unit": "kg", "dueDate": "2026-12-25" },
            { "name": "Ler mais 2 livros", "description": "Está terminando um livro", "indicator": "livros terminados",
              "startValue": null, "targetValue": 2, "unit": "livros", "dueDate": null }
          ],
          "doubts": [
            { "question": "Quantos livros a meta vai contar?",
              "options": ["3 livros: o que estou terminando + 2 novos", "2 livros: só os novos, sem contar o que estou terminando"] }
          ],
          "finished": false
        }
        """;

    [Fact]
    public async Task Period_and_goals_in_one_free_sentence_become_a_draft_waiting_for_confirmation()
    {
        AtStage(Stage.CollectGoals);
        var goals = new FakeGoalService();
        var workflow = CreateWorkflow(new FakeChatClient(TwoGoalsInOneSentence), goals);

        var reply = await workflow.ReplyAsync(Message("vamos definir objetivos até o fim desse ano, estou com 94 kg..."));

        Assert.Contains("Entendi estas 2 metas", reply);
        Assert.Contains("peso corporal: de 94 para 90 kg · até 25/12/2026", reply);
        Assert.Contains("livros terminados: 2 livros · até 31/12/2026", reply); // no date of its own → the period
        Assert.Contains("Antes de salvar, preciso que você escolha:", reply);
        Assert.Contains("❓ Quantos livros a meta vai contar?\n**A)** 3 livros: o que estou terminando + 2 novos\n**B)** 2 livros: só os novos", reply);
        Assert.EndsWith("Responda só com a letra da opção (ex.: A).", reply);
        Assert.DoesNotContain("sim", reply); // a choice is answered with a letter, never with "sim"

        var state = _store.States[UserId];
        Assert.Equal(Stage.ConfirmGoals, state.Stage);
        Assert.Equal(new DateOnly(2026, 12, 31), state.Draft!.Horizon);
        Assert.Equal(2, state.Draft.Goals.Count);
        Assert.Equal(2, Assert.Single(state.Draft.Doubts).Options.Count);
        Assert.Empty(goals.Created); // nothing is saved before the user agrees
    }

    [Fact]
    public void A_doubt_that_is_not_a_choice_is_dropped_and_letters_continue_across_doubts()
    {
        var doubts = GoalExtraction.ToDoubts(
        [
            new ExtractedDoubt("Os 2 livros são além do atual?", ["sim"]), // one option: not a choice
            new ExtractedDoubt("Quantos livros?", ["3 livros", "2 livros"]),
            new ExtractedDoubt("Qual prazo?", ["25/12", "31/12", "31/12", " "]),
        ]);

        Assert.Equal(["Quantos livros?", "Qual prazo?"], doubts.Select(d => d.Question));
        Assert.Equal(["25/12", "31/12"], doubts[1].Options); // repeated and blank options removed

        var text = GoalExtraction.Confirmation(new GoalsDraft(null, [ShownDraft().Goals[1]], doubts));
        Assert.Contains("**A)** 3 livros\n**B)** 2 livros", text);
        Assert.Contains("**C)** 25/12\n**D)** 31/12", text);
        Assert.EndsWith("Responda com uma letra para cada pergunta (ex.: A e C).", text);
    }

    [Fact]
    public async Task Only_the_period_is_kept_and_pippin_asks_for_the_goals()
    {
        AtStage(Stage.CollectGoals);
        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "aboutGoals": true, "horizonDate": "2026-12-31", "goals": [], "doubts": [], "finished": false }"""));

        var reply = await workflow.ReplyAsync(Message("até o fim do ano"));

        Assert.Contains("Quais metas você quer alcançar até 31/12/2026?", reply);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Equal(new DateOnly(2026, 12, 31), _store.States[UserId].Draft!.Horizon);
    }

    // ---------- ④ Confirming goals ----------

    [Fact]
    public async Task Confirming_saves_every_goal_through_the_goals_module_with_its_numbers()
    {
        AtStage(Stage.ConfirmGoals, ShownDraft() with { Doubts = [] });
        var goals = new FakeGoalService();
        var workflow = CreateWorkflow(new FakeChatClient(Confirmation("confirm", booksTarget: 2)), goals);

        var reply = await workflow.ReplyAsync(Message("sim, pode salvar"));

        Assert.StartsWith("Salvei! ✅", reply);
        Assert.Collection(
            goals.Created,
            weight => Assert.Equal(
                new CreateGoalRequest(UserId, "Emagrecer até 90 kg", null, "peso corporal", new DateOnly(2026, 12, 25), 94m, 90m, "kg"),
                weight),
            books => Assert.Equal((2m, "livros", new DateOnly(2026, 12, 31)), (books.TargetValue!.Value, books.Unit, books.DueDate!.Value)));

        var state = _store.States[UserId];
        Assert.Equal(Stage.DetailGoals, state.Stage);
        Assert.Equal(new DateOnly(2026, 12, 31), state.Draft!.Horizon); // the period stays for the next goals
    }

    [Fact]
    public async Task Sim_does_not_answer_a_choice_so_nothing_is_saved_and_the_choice_is_asked_again()
    {
        AtStage(Stage.ConfirmGoals, ShownDraft());
        var goals = new FakeGoalService();
        var workflow = CreateWorkflow(new FakeChatClient(Confirmation("confirm", booksTarget: 3, keepDoubt: true)), goals);

        var reply = await workflow.ReplyAsync(Message("sim"));

        Assert.StartsWith("Preciso que você escolha uma das opções antes de salvar.", reply);
        Assert.Contains("**A)** 3 livros", reply);
        Assert.Single(_store.States[UserId].Draft!.Doubts);
        Assert.Equal(Stage.ConfirmGoals, _store.States[UserId].Stage);
        Assert.Empty(goals.Created);
    }

    [Fact]
    public async Task Choosing_an_option_applies_it_and_asks_for_sim_before_saving()
    {
        AtStage(Stage.ConfirmGoals, ShownDraft());
        var goals = new FakeGoalService();
        // Even if the AI calls the choice "confirm", the code never saves in the message that answers a doubt.
        var workflow = CreateWorkflow(new FakeChatClient(Confirmation("confirm", booksTarget: 3)), goals);

        var reply = await workflow.ReplyAsync(Message("A"));

        Assert.Contains("livros terminados: 3 livros", reply);
        Assert.EndsWith("Está certo? Responda **sim** para salvar, ou me diga o que quer mudar.", reply);
        Assert.Empty(_store.States[UserId].Draft!.Doubts);
        Assert.Empty(goals.Created);
    }

    [Fact]
    public async Task A_correction_updates_the_draft_and_asks_again_without_saving()
    {
        AtStage(Stage.ConfirmGoals, ShownDraft());
        var goals = new FakeGoalService();
        var workflow = CreateWorkflow(new FakeChatClient(Confirmation("change", booksTarget: 3)), goals);

        var reply = await workflow.ReplyAsync(Message("são além do atual, então quero terminar 3"));

        Assert.Contains("livros terminados: 3 livros", reply);
        Assert.Equal(Stage.ConfirmGoals, _store.States[UserId].Stage);
        Assert.Equal(3m, _store.States[UserId].Draft!.Goals[1].TargetValue);
        Assert.Empty(goals.Created);
    }

    [Fact]
    public async Task Another_subject_while_confirming_goes_to_the_router_which_repeats_the_confirmation()
    {
        AtStage(Stage.ConfirmGoals, ShownDraft());
        var goals = new FakeGoalService();
        var workflow = CreateWorkflow(new FakeChatClient(Confirmation("other", booksTarget: 2)), goals);

        var reply = await workflow.ReplyAsync(Message("qual a previsão do tempo?"));

        Assert.Contains(FallbackRouter.BackToQuestion + "Entendi estas 2 metas", reply);
        Assert.Equal(Stage.ConfirmGoals, _store.States[UserId].Stage);
        Assert.Empty(goals.Created);
    }

    private static GoalsDraft ShownDraft() =>
        new(
            new DateOnly(2026, 12, 31),
            [
                new GoalDraft("Emagrecer até 90 kg", null, "peso corporal", 94m, 90m, "kg", new DateOnly(2026, 12, 25)),
                new GoalDraft("Ler mais 2 livros", "Está terminando um livro", "livros terminados", null, 2m, "livros", new DateOnly(2026, 12, 31)),
            ],
            [new GoalDoubt("Quantos livros a meta vai contar?", ["3 livros: o que estou terminando + 2 novos", "2 livros: só os novos"])]);

    // The AI's "doubts" when the user's answer did not choose any option.
    private const string KeptDoubt =
        """[{ "question": "Quantos livros a meta vai contar?", "options": ["3 livros: o que estou terminando + 2 novos", "2 livros: só os novos"] }]""";

    private static string Confirmation(string decision, int booksTarget, bool keepDoubt = false) =>
        $$"""
        {
          "decision": "{{decision}}",
          "goals": [
            { "name": "Emagrecer até 90 kg", "description": null, "indicator": "peso corporal",
              "startValue": 94, "targetValue": 90, "unit": "kg", "dueDate": "2026-12-25" },
            { "name": "Ler mais {{booksTarget}} livros", "description": "Está terminando um livro", "indicator": "livros terminados",
              "startValue": null, "targetValue": {{booksTarget}}, "unit": "livros", "dueDate": "2026-12-31" }
          ],
          "doubts": {{(keepDoubt ? KeptDoubt : "[]")}}
        }
        """;

    // ---------- ⑤ Deepening the saved goals: one question each, then the plan for confirmation ----------

    private const string ReadingQuestionText =
        "Para conseguir **Ler 2 livros**, qual o seu plano? Me diga em poucas palavras, ou se quiser eu posso propor um plano para você.";

    private const string ReadingQuestion = $$"""{ "question": "{{ReadingQuestionText}}" }""";

    private const string WeightQuestionText =
        "Para chegar a **90 kg** até o Natal, quanto você pesa hoje e qual o seu plano? Me diga em poucas palavras, ou se quiser eu posso propor um plano para você.";

    private const string WeightQuestion = $$"""{ "question": "{{WeightQuestionText}}" }""";

    // "seg, qua e sex de manhã, 10 páginas", as the AI breaks it into a routine.
    private const string MorningReading =
        """
        [{ "activity": "ler", "frequency": "weekly", "interval": 1, "daysOfWeek": [1, 3, 5], "timesPerPeriod": null,
           "timeOfDay": null, "dayPeriod": "morning", "amount": 10, "unit": "páginas", "durationMinutes": null }]
        """;

    private const string NightReading =
        """[{ "activity": "ler", "frequency": "weekly", "daysOfWeek": [1, 3, 5], "dayPeriod": "night", "amount": 10, "unit": "páginas" }]""";

    private static readonly GoalDraft Reading =
        new("Ler 2 livros", null, "livros terminados", null, 2m, "livros", new DateOnly(2026, 12, 31));

    private static readonly RoutineInput MorningRoutine = new(
        "ler", RoutineFrequency.Weekly, RoutineSource.User, DaysOfWeek: [1, 3, 5], DayPeriod: DayPeriod.Morning,
        Amount: 10m, Unit: "páginas");

    [Fact]
    public async Task Confirming_saves_the_goals_and_goes_straight_to_the_one_question_about_the_first()
    {
        AtStage(Stage.ConfirmGoals, ShownDraft() with { Doubts = [] });
        var goals = new FakeGoalService();
        var ai = new FakeChatClient(Confirmation("confirm", booksTarget: 2), WeightQuestion);
        var workflow = CreateWorkflow(ai, goals);

        var reply = await workflow.ReplyAsync(Message("sim"));

        Assert.StartsWith("Salvei! ✅", reply);
        Assert.Contains("Agora vamos ver como você vai chegar lá, uma meta de cada vez.", reply);
        Assert.EndsWith(WeightQuestionText, reply);
        Assert.DoesNotContain("Quer adicionar mais", reply); // no "more goals?" before deepening

        var state = _store.States[UserId];
        Assert.Equal(Stage.DetailGoals, state.Stage);
        Assert.Equal(goals.Saved.Select(g => g.Id), state.Draft!.DetailQueue); // every goal, not only the incomplete
        Assert.Equal(WeightQuestionText, state.Draft.Question);
        Assert.Null(state.Draft.Plan);
    }

    [Fact]
    public async Task When_the_ai_cannot_write_the_question_pippin_still_asks_for_the_plan()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtStage(Stage.ConfirmGoals, new GoalsDraft(new DateOnly(2026, 12, 31), [Reading], []));
        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "decision": "confirm", "goals": [{ "name": "Ler 2 livros" }], "doubts": [] }""", "not json"), goals);

        var reply = await workflow.ReplyAsync(Message("sim"));

        Assert.EndsWith(ReadingQuestionText, reply);
    }

    [Fact]
    public async Task The_answer_becomes_routines_and_the_users_own_words_shown_for_confirmation()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading);
        var workflow = CreateWorkflow(new FakeChatClient(
            DetailAnswer("answer", MorningReading, assumptions: """["10 páginas por vez terminam 2 livros"]""")), goals);

        var reply = await workflow.ReplyAsync(Message("vou ler segunda, quarta e sexta de manhã, umas 10 páginas"));

        Assert.Equal(
            "Entendi assim o plano de **Ler 2 livros**:\n" +
            "- **Meta:** 2 livros · até 31/12/2026\n" +
            "- **Rotina:** ler — segunda, quarta e sexta · de manhã · 10 páginas\n\n" +
            "💭 Supus: 10 páginas por vez terminam 2 livros.\n\n" +
            "Está certo? Responda **sim** para salvar, ou me diga o que ajustar.",
            reply);

        var draft = _store.States[UserId].Draft!;
        Assert.Equal("vou ler segunda, quarta e sexta de manhã, umas 10 páginas", draft.Goals[0].UserPlan);
        Assert.Equal(MorningRoutine, Assert.Single(draft.Plan!.Routines) with { DaysOfWeek = MorningRoutine.DaysOfWeek });
        Assert.Equal([1, 3, 5], draft.Plan.Routines[0].DaysOfWeek);
        Assert.Equal(RoutineSource.User, draft.Plan.Source);
        Assert.Empty(goals.Updated); // nothing is saved before the confirmation
        Assert.Empty(goals.RoutinesReplaced);
    }

    [Fact]
    public async Task The_current_value_goes_to_the_start_value_and_flexible_days_to_times_per_week()
    {
        var goals = new FakeGoalService();
        var weight = goals.Add(UserId, "Emagrecer até 90 kg", November.GetUtcNow());
        AtDetailing(weight, goal: new GoalDraft("Emagrecer até 90 kg", null, "peso corporal", null, 90m, "kg", new DateOnly(2026, 12, 25)));
        var workflow = CreateWorkflow(new FakeChatClient(
            """
            { "decision": "answer",
              "goal": { "name": "Emagrecer até 90 kg", "indicator": "peso corporal", "startValue": 97, "targetValue": 90,
                        "unit": "kg", "dueDate": "2026-12-25" },
              "routines": [{ "activity": "ir à academia", "frequency": "weekly", "daysOfWeek": [], "timesPerPeriod": 3,
                             "durationMinutes": 60 }],
              "assumptions": [] }
            """), goals);

        var reply = await workflow.ReplyAsync(Message("peso 97 e vou na academia 3 vezes por semana"));

        Assert.Contains("- **Hoje:** 97 kg\n- **Meta:** 90 kg · até 25/12/2026\n- **Rotina:** ir à academia — 3x por semana · 60 min", reply);
        Assert.Equal(97m, _store.States[UserId].Draft!.Goals[0].StartValue);
    }

    [Fact]
    public async Task Asking_for_a_proposal_shows_pippins_plan_without_putting_words_in_the_users_mouth()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading);
        var workflow = CreateWorkflow(new FakeChatClient(DetailAnswer("propose", MorningReading)), goals);

        var reply = await workflow.ReplyAsync(Message("pode propor"));

        Assert.StartsWith("Minha proposta de plano para **Ler 2 livros**:", reply);
        var draft = _store.States[UserId].Draft!;
        Assert.Equal(RoutineSource.Pippin, draft.Plan!.Source);
        Assert.Equal(RoutineSource.Pippin, draft.Plan.Routines[0].Source);
        Assert.Null(draft.Goals[0].UserPlan);
    }

    [Fact]
    public async Task Sim_saves_the_goal_its_plan_and_routines_and_with_none_left_shows_the_summary()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        goals.Add(UserId, "Meditar", November.GetUtcNow());
        AtDetailing(reading, plan: true);
        var workflow = CreateWorkflow(new FakeChatClient(DetailAnswer("confirm")), goals);

        var reply = await workflow.ReplyAsync(Message("sim"));

        Assert.Equal(
            new UpdateGoalRequest(reading.Id, UserId, "Ler 2 livros", null, "livros terminados", new DateOnly(2026, 12, 31),
                null, 2m, "livros", "vou ler seg, qua e sex de manhã"),
            Assert.Single(goals.Updated));
        var (goalId, routines) = Assert.Single(goals.RoutinesReplaced);
        Assert.Equal(reading.Id, goalId);
        // A routine runs from today until the goal's due date.
        Assert.Equal((new DateOnly(2026, 11, 13), new DateOnly(2026, 12, 31)), (routines[0].StartsOn!.Value, routines[0].EndsOn!.Value));

        Assert.Equal(
            "Salvei o plano de **Ler 2 livros**! ✅\n\nPronto, terminamos! 🎉\n\n" +
            "**Resumo das suas metas:**\n" +
            "1. **Ler 2 livros** — livros terminados: 2 livros · até 31/12/2026\n" +
            "2. **Meditar** — indicador: A ser determinado · prazo: A ser determinado\n\n" +
            "Esse é o resumo das metas. Se quiser as informações completas sobre alguma delas, é só falar.\n\n" +
            "Quer adicionar mais alguma meta até 31/12/2026?",
            reply);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Null(_store.States[UserId].Draft!.Plan);
    }

    [Fact]
    public async Task Sim_goes_on_to_the_one_question_about_the_next_goal()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        var weight = goals.Add(UserId, "Emagrecer até 90 kg", November.GetUtcNow());
        AtDetailing(reading, weight, plan: true);
        var workflow = CreateWorkflow(new FakeChatClient(DetailAnswer("confirm"), WeightQuestion), goals);

        var reply = await workflow.ReplyAsync(Message("isso"));

        Assert.Equal($"Salvei o plano de **Ler 2 livros**! ✅\n\n{WeightQuestionText}", reply);
        var draft = _store.States[UserId].Draft!;
        Assert.Equal([weight.Id], draft.DetailQueue);
        Assert.Equal("Emagrecer até 90 kg", draft.Goals[0].Name);
        Assert.Null(draft.Plan);
    }

    [Fact]
    public async Task A_correction_shows_the_plan_again_and_keeps_every_word_the_user_wrote()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading, plan: true);
        var workflow = CreateWorkflow(new FakeChatClient(DetailAnswer("change", NightReading)), goals);

        var reply = await workflow.ReplyAsync(Message("na verdade prefiro à noite"));

        Assert.Contains("- **Rotina:** ler — segunda, quarta e sexta · à noite · 10 páginas", reply);
        Assert.Equal(
            "vou ler seg, qua e sex de manhã\nna verdade prefiro à noite", _store.States[UserId].Draft!.Goals[0].UserPlan);
        Assert.Empty(goals.Updated);
    }

    [Fact]
    public async Task Skipping_leaves_the_goal_for_later_and_moves_on()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading);
        var workflow = CreateWorkflow(new FakeChatClient(DetailAnswer("skip")), goals);

        var reply = await workflow.ReplyAsync(Message("depois eu vejo"));

        Assert.StartsWith("Tudo bem! Quando quiser, é só me contar o plano de **Ler 2 livros**.\n\nPronto, terminamos!", reply);
        Assert.Empty(goals.Updated);
        Assert.Empty(goals.RoutinesReplaced);
    }

    [Fact]
    public async Task Ignoring_the_confirmation_counts_as_sim_and_the_message_is_still_handled()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading, plan: true);
        var workflow = CreateWorkflow(new FakeChatClient(
            DetailAnswer("other"),
            """{ "aboutGoals": false, "isGreeting": false, "goals": [], "doubts": [], "finished": false }""", // CollectGoals
            """{ "route": "out_of_scope", "reason": "previsão do tempo" }"""), goals);

        var reply = await workflow.ReplyAsync(Message("qual a previsão do tempo?"));

        Assert.Single(goals.Updated);
        Assert.Single(goals.RoutinesReplaced);
        Assert.StartsWith("Salvei o plano de **Ler 2 livros**! ✅\n\nPronto, terminamos! 🎉", reply);
        Assert.EndsWith(FallbackRouter.BackToQuestion + "Quer adicionar mais alguma meta até 31/12/2026?", reply);
        Assert.Single(Occurrences(reply, "Quer adicionar mais")); // the summary leaves the question to the router
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Ignoring_the_confirmation_with_more_goals_saves_and_asks_about_the_next_one()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        var weight = goals.Add(UserId, "Emagrecer até 90 kg", November.GetUtcNow());
        AtDetailing(reading, weight, plan: true);
        var workflow = CreateWorkflow(new FakeChatClient(
            DetailAnswer("other"),
            WeightQuestion,
            DetailAnswer("other"), // the next goal's node does not understand it either
            """{ "route": "out_of_scope", "reason": "piada" }"""), goals);

        var reply = await workflow.ReplyAsync(Message("me conta uma piada"));

        Assert.StartsWith("Salvei o plano de **Ler 2 livros**! ✅\n\n", reply);
        Assert.EndsWith(FallbackRouter.BackToQuestion + WeightQuestionText, reply);
        Assert.Equal([weight.Id], _store.States[UserId].Draft!.DetailQueue);
        Assert.Equal(Stage.DetailGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Ignoring_the_confirmation_with_a_new_goal_saves_the_plan_and_collects_the_new_goal()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading, plan: true);
        var ai = new FakeChatClient(DetailAnswer("other"), NewGoalRunning);
        var workflow = CreateWorkflow(ai, goals);

        var reply = await workflow.ReplyAsync(Message("quero adicionar outra meta: correr 5 km"));

        Assert.Single(goals.RoutinesReplaced);
        Assert.StartsWith("Salvei o plano de **Ler 2 livros**! ✅\n\nPronto, terminamos!", reply);
        Assert.Contains("Entendi esta meta:\n1. **Correr 5 km**", reply);
        Assert.Equal(Stage.ConfirmGoals, _store.States[UserId].Stage);
        Assert.Equal(2, ai.Calls.Count); // CollectGoals understood it: no classifier needed
    }

    [Fact]
    public async Task A_greeting_while_detailing_resumes_the_plan_waiting_for_confirmation()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading, plan: true);
        var workflow = CreateWorkflow(new FakeChatClient(DetailAnswer("greeting")), goals);

        var reply = await workflow.ReplyAsync(Message("oi"));

        Assert.StartsWith("Opa! Olá, Rafael! 😊 Voltando de onde paramos:\n\nEntendi assim o plano de **Ler 2 livros**:", reply);
        Assert.Empty(goals.Updated); // a greeting is not an ignored confirmation
    }

    [Fact]
    public async Task A_greeting_before_answering_repeats_the_question()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading);
        var workflow = CreateWorkflow(new FakeChatClient(DetailAnswer("greeting")), goals);

        var reply = await workflow.ReplyAsync(Message("bom dia"));

        Assert.Equal($"Opa! Olá, Rafael! 😊 {ReadingQuestionText}", reply);
    }

    // ---------- The path classifier: when a node does not understand, the AI picks the way on ----------

    private const string NewGoalRunning =
        """
        { "aboutGoals": true, "isGreeting": false, "horizonDate": null,
          "goals": [{ "name": "Correr 5 km", "indicator": "distância numa corrida", "targetValue": 5, "unit": "km",
                      "dueDate": "2026-12-31" }],
          "doubts": [], "finished": false }
        """;

    [Fact]
    public async Task A_new_goal_while_detailing_jumps_to_collecting_with_the_same_message()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading);
        var ai = new FakeChatClient(
            DetailAnswer("other"),                                   // DetailGoals: not about this goal
            """{ "route": "goto", "stage": "CollectGoals", "reason": "meta nova" }""", // the classifier
            NewGoalRunning);                                                    // CollectGoals, same message
        var workflow = CreateWorkflow(ai, goals);

        var reply = await workflow.ReplyAsync(Message("na verdade quero adicionar outra meta: correr 5 km"));

        Assert.StartsWith("Entendi esta meta:\n1. **Correr 5 km**", reply);
        Assert.Equal(Stage.ConfirmGoals, _store.States[UserId].Stage);
        Assert.Null(_store.States[UserId].Draft!.DetailQueue); // the detailing in progress was left behind
        Assert.Equal(3, ai.Calls.Count);
    }

    [Fact]
    public async Task Talking_about_a_saved_goal_jumps_to_detailing_that_goal()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var goals = new FakeGoalService();
        goals.Add(UserId, "Emagrecer até 90 kg", November.GetUtcNow());
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        var ai = new FakeChatClient(
            """{ "aboutGoals": false, "isGreeting": false, "goals": [], "doubts": [], "finished": false }""",
            """{ "route": "goto", "stage": "DetailGoals", "goalNumber": 2, "reason": "quer falar dos livros" }""",
            ReadingQuestion);
        var workflow = CreateWorkflow(ai, goals);

        var reply = await workflow.ReplyAsync(Message("quero detalhar melhor a meta dos livros"));

        Assert.Equal($"Claro!\n\n{ReadingQuestionText}", reply);
        Assert.Equal(Stage.DetailGoals, _store.States[UserId].Stage);
        Assert.Equal([reading.Id], _store.States[UserId].Draft!.DetailQueue);
    }

    [Fact]
    public async Task A_jump_the_code_does_not_allow_becomes_a_clarification_and_the_stage_stays()
    {
        AtStage(Stage.ConfirmGoals, ShownDraft());
        var goals = new FakeGoalService();
        var workflow = CreateWorkflow(new FakeChatClient(
            Confirmation("other", booksTarget: 2),
            """{ "route": "goto", "stage": "DetailGoals", "goalNumber": 1, "reason": "detalhar" }"""), goals);

        var reply = await workflow.ReplyAsync(Message("quero detalhar essas metas"));

        // ConfirmGoals → DetailGoals would detail goals that are not saved yet: refused by code.
        Assert.StartsWith("Acho que não entendi direito. 🤔\n\nEntendi estas 2 metas", reply);
        Assert.Equal(Stage.ConfirmGoals, _store.States[UserId].Stage);
        Assert.Empty(goals.Created);
    }

    [Fact]
    public async Task An_unclear_answer_gets_the_classifiers_clarification_and_the_question_again()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "aboutGoals": false, "isGreeting": false, "goals": [], "doubts": [], "finished": false }""",
            """{ "route": "clarify", "clarification": "Preciso saber qual meta você quer alcançar.", "reason": "vago" }"""));

        var reply = await workflow.ReplyAsync(Message("hmm talvez aquilo"));

        Assert.Equal("Preciso saber qual meta você quer alcançar.\n\nQuais metas você quer alcançar até 31/12/2026?", reply);
    }

    [Fact]
    public async Task When_the_destination_does_not_understand_either_nothing_changes_and_pippin_asks_again()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtDetailing(reading);
        var workflow = CreateWorkflow(new FakeChatClient(
            DetailAnswer("other"),
            """{ "route": "goto", "stage": "CollectGoals", "reason": "talvez meta nova" }""",
            """{ "aboutGoals": false, "isGreeting": false, "goals": [], "doubts": [], "finished": false }"""), goals);

        var reply = await workflow.ReplyAsync(Message("e aquela coisa"));

        Assert.Equal($"Acho que não entendi direito. 🤔\n\n{ReadingQuestionText}", reply);
        Assert.Equal(Stage.DetailGoals, _store.States[UserId].Stage);
        Assert.Equal([reading.Id], _store.States[UserId].Draft!.DetailQueue); // the detailing is still there
    }

    [Fact]
    public async Task Out_of_scope_keeps_the_funny_reply_and_the_question()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "aboutGoals": false, "isGreeting": false, "goals": [], "doubts": [], "finished": false }""",
            """{ "route": "out_of_scope", "reason": "previsão do tempo" }"""));

        var reply = await workflow.ReplyAsync(Message("qual a previsão do tempo?"));

        Assert.EndsWith(FallbackRouter.BackToQuestion + "Quais metas você quer alcançar até 31/12/2026?", reply);
    }

    // ---------- The goals agent: consult, change, cancel and record progress (tools + loop) ----------

    // What CollectGoals' AI returns for a message about a saved goal: not a new goal, not about the period.
    private const string AboutASavedGoal =
        """{ "aboutGoals": false, "isGreeting": false, "goals": [], "doubts": [], "finished": false }""";

    private const string GoalTalk = """{ "route": "goal_talk", "reason": "progresso de uma meta salva" }""";

    [Fact]
    public async Task Reported_progress_goes_through_the_agent_tool_and_is_recorded_for_the_right_goal()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var goals = new FakeGoalService();
        goals.Add(UserId, "Emagrecer até 90 kg", November.GetUtcNow().AddDays(-2));
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow().AddDays(-1));
        var progress = new FakeProgressService();
        var ai = new FakeChatClient(
            FakeChatClient.Text(AboutASavedGoal),
            FakeChatClient.Text(GoalTalk),
            // The model asks for the tool; the loop runs it and calls the model again with the result.
            FakeChatClient.ToolCall("record_progress", new()
            {
                ["goalNumber"] = 2, ["note"] = "leu 10 páginas", ["amount"] = 10, ["unit"] = "páginas",
            }),
            FakeChatClient.Text("Boa! 📚 Registrei 10 páginas em **Ler 2 livros**."));

        var reply = await CreateWorkflow(ai, goals, progress).ReplyAsync(Message("hoje li 10 páginas"));

        var entry = Assert.Single(progress.Entries);
        Assert.Equal(reading.Id, entry.GoalId);
        Assert.Equal(UserId, entry.UserId);
        Assert.Equal(10m, entry.Amount);
        Assert.Equal("páginas", entry.Unit);
        Assert.Equal(new DateOnly(2026, 11, 13), entry.OccurredOn); // "hoje", in the user's time zone

        // Between tasks the agent's reply stands alone: no "quer adicionar mais metas?" after it.
        Assert.Equal("Boa! 📚 Registrei 10 páginas em **Ler 2 livros**.", reply);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);

        // The second call to the model carried the tool's result, with the totals it can celebrate.
        var result = ai.Calls[3].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("Total em páginas: 10", result.Result?.ToString());
    }

    [Fact]
    public async Task A_tool_called_with_a_goal_number_that_does_not_exist_changes_nothing()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var goals = new FakeGoalService();
        goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        var progress = new FakeProgressService();
        var ai = new FakeChatClient(
            FakeChatClient.Text(AboutASavedGoal),
            FakeChatClient.Text(GoalTalk),
            FakeChatClient.ToolCall("record_progress", new() { ["goalNumber"] = 7, ["note"] = "correu 5 km" }),
            FakeChatClient.Text("Não achei essa meta."));

        await CreateWorkflow(ai, goals, progress).ReplyAsync(Message("corri 5 km"));

        Assert.Empty(progress.Entries);
        var result = ai.Calls[3].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.StartsWith("Erro:", result.Result?.ToString());
    }

    [Fact]
    public async Task Changing_a_due_date_updates_only_that_field()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var goals = new FakeGoalService();
        var weight = goals.Add(UserId, "Emagrecer até 90 kg", November.GetUtcNow());
        var ai = new FakeChatClient(
            FakeChatClient.Text(AboutASavedGoal),
            FakeChatClient.Text(GoalTalk),
            FakeChatClient.ToolCall("update_goal", new() { ["goalNumber"] = 1, ["dueDate"] = "2027-03-31" }),
            FakeChatClient.Text("Pronto, o prazo agora é 31/03/2027."));

        await CreateWorkflow(ai, goals).ReplyAsync(Message("muda o prazo do peso para março"));

        var update = Assert.Single(goals.Updated);
        Assert.Equal(weight.Id, update.GoalId);
        Assert.Equal(new DateOnly(2027, 3, 31), update.DueDate);
        Assert.Equal("Emagrecer até 90 kg", update.Name); // the rest stays as it was
    }

    [Fact]
    public async Task Asking_to_cancel_does_not_cancel_it_asks_for_an_explicit_sim_first()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        var ai = new FakeChatClient(
            FakeChatClient.Text(AboutASavedGoal),
            FakeChatClient.Text(GoalTalk),
            FakeChatClient.ToolCall("request_goal_cancellation", new() { ["goalNumber"] = 1 }),
            FakeChatClient.Text("Vou pedir a confirmação."),
            FakeChatClient.Text("""{ "decision": "confirm" }"""));
        var workflow = CreateWorkflow(ai, goals);

        var question = await workflow.ReplyAsync(Message("a meta de leitura tem que ser cancelada"));

        Assert.Equal(ConfirmCancelGoalNode.Question("Ler 2 livros"), question); // the code asks, not the model
        Assert.Empty(goals.Cancelled);
        Assert.Equal(Stage.ConfirmCancelGoal, _store.States[UserId].Stage);

        var reply = await workflow.ReplyAsync(Message("sim"));

        Assert.Equal([reading.Id], goals.Cancelled);
        Assert.Contains("cancelei a meta **Ler 2 livros**", reply);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Null(_store.States[UserId].Draft!.CancelGoalId);
    }

    [Fact]
    public async Task Saying_no_to_the_cancellation_keeps_the_goal()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtStage(Stage.ConfirmCancelGoal, GoalsDraft.Empty(new DateOnly(2026, 12, 31)) with
        {
            CancelGoalId = reading.Id, ReturnStage = Stage.CollectGoals,
        });

        var reply = await CreateWorkflow(new FakeChatClient("""{ "decision": "keep" }"""), goals).ReplyAsync(Message("não"));

        Assert.Empty(goals.Cancelled);
        Assert.Contains("continua de pé", reply);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Cancelling_another_goal_while_detailing_resumes_the_detailing_without_it()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow().AddDays(-1));
        var weight = goals.Add(UserId, "Emagrecer até 90 kg", November.GetUtcNow());
        AtDetailing(reading, weight);
        var ai = new FakeChatClient(
            FakeChatClient.Text(DetailAnswer("other")),
            FakeChatClient.Text(GoalTalk),
            FakeChatClient.ToolCall("request_goal_cancellation", new() { ["goalNumber"] = 2 }),
            FakeChatClient.Text("ok"),
            FakeChatClient.Text("""{ "decision": "confirm" }"""));
        var workflow = CreateWorkflow(ai, goals);

        await workflow.ReplyAsync(Message("na verdade quero cancelar a meta do peso"));
        var reply = await workflow.ReplyAsync(Message("sim, pode cancelar"));

        Assert.Equal([weight.Id], goals.Cancelled);
        Assert.Equal(Stage.DetailGoals, _store.States[UserId].Stage);
        Assert.Equal([reading.Id], _store.States[UserId].Draft!.DetailQueue); // the weight left the queue
        Assert.EndsWith(FallbackRouter.BackToQuestion + ReadingQuestionText, reply); // back to the reading question
    }

    [Fact]
    public async Task An_unclear_answer_to_the_cancellation_does_not_cancel()
    {
        var goals = new FakeGoalService();
        var reading = goals.Add(UserId, "Ler 2 livros", November.GetUtcNow());
        AtStage(Stage.ConfirmCancelGoal, GoalsDraft.Empty(new DateOnly(2026, 12, 31)) with
        {
            CancelGoalId = reading.Id, ReturnStage = Stage.CollectGoals,
        });
        var ai = new FakeChatClient(
            """{ "decision": "other" }""",
            """{ "aboutGoals": false, "isGreeting": false, "goals": [], "doubts": [], "finished": false }""",
            """{ "route": "out_of_scope", "reason": "outro assunto" }""");

        var reply = await CreateWorkflow(ai, goals).ReplyAsync(Message("qual a previsão do tempo?"));

        Assert.Empty(goals.Cancelled);
        Assert.StartsWith("Não cancelei a meta **Ler 2 livros**.", reply);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Goal_talk_without_saved_goals_is_out_of_scope()
    {
        AtStage(Stage.CollectGoals, GoalsDraft.Empty(new DateOnly(2026, 12, 31)));
        var ai = new FakeChatClient(AboutASavedGoal, GoalTalk);

        var reply = await CreateWorkflow(ai).ReplyAsync(Message("hoje li 10 páginas"));

        Assert.EndsWith(FallbackRouter.BackToQuestion + "Quais metas você quer alcançar até 31/12/2026?", reply);
        Assert.Equal(2, ai.Calls.Count); // the agent was never called
    }

    // The conversation deepening the first goal of the queue: waiting for the answer to the one question,
    // or, with a plan, waiting for "sim" to it.
    private void AtDetailing(GoalDto first, GoalDto? second = null, bool plan = false, GoalDraft? goal = null) =>
        AtStage(Stage.DetailGoals, new GoalsDraft(
            new DateOnly(2026, 12, 31),
            [plan ? (goal ?? Reading) with { UserPlan = "vou ler seg, qua e sex de manhã" } : goal ?? Reading],
            [],
            second is null ? [first.Id] : [first.Id, second.Id],
            Question: goal is null ? ReadingQuestionText : WeightQuestionText,
            Plan: plan ? new PlanDraft([MorningRoutine], RoutineSource.User) : null));

    private static string DetailAnswer(string decision, string routines = "[]", string assumptions = "[]") =>
        $$"""
        {
          "decision": "{{decision}}",
          "goal": { "name": "Ler 2 livros", "description": null, "indicator": "livros terminados",
                    "startValue": null, "targetValue": 2, "unit": "livros", "dueDate": "2026-12-31" },
          "routines": {{routines}},
          "assumptions": {{assumptions}}
        }
        """;

    private void AtStage(Stage stage, GoalsDraft? draft = null) =>
        _store.States[UserId] = new ConversationState(stage, "Rafael", draft);

    private readonly FakePlanningService _planning = new();
    private readonly FakePippinChannel _channel = new();

    private PippinWorkflow CreateWorkflow(IChatClient ai, FakeGoalService? goals = null, FakeProgressService? progress = null)
    {
        goals ??= new FakeGoalService();
        progress ??= new FakeProgressService();
        var detailing = new GoalDetailing(ai, goals, _planning, November);
        var review = new PlanReview(_planning, goals);
        return new(
            _store,
            [
                new GreetingNode(),
                new AskNameNode(ai, November),
                new CollectGoalsNode(ai, November),
                new ConfirmGoalsNode(ai, goals, detailing, November),
                new DetailGoalsNode(ai, detailing, November),
                new ConfirmCancelGoalNode(ai, goals, detailing),
                new OfferPlanNode(ai, review),
                new ChoosePlanGoalNode(ai, review),
                new RefinePlanGoalNode(ai, review, detailing, _planning, November),
            ],
            new FallbackRouter(),
            new GreetingReply(),
            new PathClassifier(ai),
            new GoalsAgent(ai, goals, progress, November, NullLoggerFactory.Instance),
            goals,
            _channel,
            NullLogger<PippinWorkflow>.Instance);
    }

    private static PippinMessage Message(string text) => new(UserId, "rafael", text);

    private static IEnumerable<int> Occurrences(string text, string part)
    {
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
        {
            yield return i;
        }
    }
}
