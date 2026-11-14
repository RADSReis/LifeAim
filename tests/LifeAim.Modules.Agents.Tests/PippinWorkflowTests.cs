using LifeAim.Modules.Agents.Application.Nodes;
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
        Assert.Equal(Stage.CollectGoals, state.Stage);
        Assert.Empty(state.Draft!.Goals);
        Assert.Equal(new DateOnly(2026, 12, 31), state.Draft.Horizon); // the period stays for the next goals
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

    private void AtStage(Stage stage, GoalsDraft? draft = null) =>
        _store.States[UserId] = new ConversationState(stage, "Rafael", draft);

    private PippinWorkflow CreateWorkflow(IChatClient ai, FakeGoalService? goals = null) =>
        new(
            _store,
            [
                new GreetingNode(),
                new AskNameNode(ai, November),
                new CollectGoalsNode(ai, November),
                new ConfirmGoalsNode(ai, goals ?? new FakeGoalService(), November),
            ],
            new FallbackRouter(),
            NullLogger<PippinWorkflow>.Instance);

    private static PippinMessage Message(string text) => new(UserId, "rafael", text);

    private static IEnumerable<int> Occurrences(string text, string part)
    {
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
        {
            yield return i;
        }
    }
}
