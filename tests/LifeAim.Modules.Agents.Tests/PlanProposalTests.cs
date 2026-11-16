using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>Funcionalidade 2: the plan proposal prepared in the background, offered and refined one goal at a time.</summary>
public class PlanProposalTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    // 12:00 in São Paulo: outside the quiet hours.
    private static readonly FixedTimeProvider Noon = new(new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero));

    // 23:00 in São Paulo: Pippin does not start conversations.
    private static readonly FixedTimeProvider Night = new(new DateTimeOffset(2026, 11, 14, 2, 0, 0, TimeSpan.Zero));

    private readonly InMemoryConversationStore _store = new();
    private readonly FakeGoalService _goals = new();
    private readonly FakePlanningService _planning = new();
    private readonly FakePippinChannel _channel = new();

    [Fact]
    public async Task Ending_the_intake_asks_for_the_proposal_and_says_it_will_be_prepared()
    {
        var goal = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        AtStage(Stage.DetailGoals, Detailing(goal, proposeAfter: true));
        var workflow = CreateWorkflow(new FakeChatClient(Confirm));

        var reply = await workflow.ReplyAsync(Message("sim"));

        Assert.Contains(GoalDetailing.PreparingProposal, reply);
        Assert.DoesNotContain("Quer adicionar mais", reply);
        Assert.Equal(ProposalStatus.Requested, Assert.Single(_planning.Proposals).Status);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Detailing_one_goal_later_does_not_ask_for_a_new_proposal()
    {
        var goal = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        AtStage(Stage.DetailGoals, Detailing(goal, proposeAfter: false));
        var workflow = CreateWorkflow(new FakeChatClient(Confirm));

        var reply = await workflow.ReplyAsync(Message("sim"));

        Assert.Contains("Quer adicionar mais", reply);
        Assert.Empty(_planning.Proposals);
    }

    [Fact]
    public async Task Saving_new_goals_marks_the_detailing_to_end_with_a_proposal()
    {
        AtStage(Stage.ConfirmGoals, new GoalsDraft(null, [new GoalDraft("Ler 2 livros", null, null, null, 2, "livros", null)], []));
        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "decision": "confirm", "goals": [ { "name": "Ler 2 livros", "targetValue": 2, "unit": "livros" } ], "doubts": [] }""",
            """{ "question": "Qual o seu plano?" }"""));

        await workflow.ReplyAsync(Message("sim"));

        Assert.True(_store.States[UserId].Draft!.ProposeAfter);
    }

    [Fact]
    public async Task The_job_writes_the_proposal_and_offers_it_when_the_user_is_free()
    {
        var books = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        var gym = _goals.Add(UserId, "Academia 3x por semana", DateTimeOffset.UtcNow);
        await _planning.RequestAsync(UserId);
        AtStage(Stage.CollectGoals);
        var job = CreateJob(new FakeChatClient(TwoGoalProposal), Noon);

        await job.RunAsync(CancellationToken.None);

        var proposal = Assert.Single(_planning.Proposals);
        Assert.Equal(ProposalStatus.Offered, proposal.Status);
        Assert.Equal([gym.Id, books.Id], proposal.Goals.Select(g => g.GoalId));
        Assert.Equal(RoutineFrequency.Weekly, proposal.Goals[0].Routines.Single().Frequency);

        var (to, text) = Assert.Single(_channel.Sent);
        Assert.Equal(UserId, to);
        Assert.Equal(PlanReview.Invitation, text);
        Assert.Equal(Stage.OfferPlan, _store.States[UserId].Stage);
        Assert.Equal(proposal.Id, _store.States[UserId].Draft!.ProposalId);
        Assert.Equal(PlanReview.Invitation, _store.Messages.Last().Turn.Content);
    }

    [Fact]
    public async Task The_invitation_waits_while_the_user_is_in_the_middle_of_something()
    {
        var goal = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        _planning.AddReady(UserId, Proposal(goal));
        AtStage(Stage.DetailGoals, Detailing(goal, proposeAfter: false));

        await CreateJob(new FakeChatClient("unused"), Noon).RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
        Assert.Equal(ProposalStatus.Ready, _planning.Proposals.Single().Status);
        Assert.Equal(Stage.DetailGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task The_invitation_waits_for_the_morning()
    {
        var goal = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        _planning.AddReady(UserId, Proposal(goal));
        AtStage(Stage.CollectGoals);

        await CreateJob(new FakeChatClient("unused"), Night).RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
        Assert.Equal(ProposalStatus.Ready, _planning.Proposals.Single().Status);
    }

    [Fact]
    public async Task Nothing_delivered_means_nothing_marked_as_offered()
    {
        var goal = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        _planning.AddReady(UserId, Proposal(goal));
        AtStage(Stage.CollectGoals);
        _channel.Delivers = false;

        await CreateJob(new FakeChatClient("unused"), Noon).RunAsync(CancellationToken.None);

        Assert.Equal(ProposalStatus.Ready, _planning.Proposals.Single().Status);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task An_unreadable_ai_answer_counts_as_a_failed_attempt()
    {
        _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        await _planning.RequestAsync(UserId);

        await CreateJob(new FakeChatClient("not json"), Noon).RunAsync(CancellationToken.None);

        var proposal = Assert.Single(_planning.Proposals);
        Assert.Equal(ProposalStatus.Requested, proposal.Status);
        Assert.Equal(1, proposal.Attempts);
    }

    [Fact]
    public void Only_known_goals_once_each_with_message_and_question_become_proposals()
    {
        var goals = new[] { Goal("Ler"), Goal("Correr") };
        var answer = new PlanProposer.ProposalAnswer(
        [
            new(2, "Correr 3x", "Quando?", []),
            new(2, "repetida", "?", []),
            new(7, "não existe", "?", []),
            new(1, " ", "sem mensagem", []),
        ]);

        var proposals = PlanProposer.ToProposals(answer, goals);

        Assert.Equal(goals[1].Id, Assert.Single(proposals).GoalId);
    }

    [Fact]
    public async Task Going_through_the_proposal_three_goals_at_a_time_one_in_focus()
    {
        var goals = new[] { "Ler 2 livros", "Academia 3x por semana", "Aprender inglês", "Meditar" }
            .Select(name => _goals.Add(UserId, name, DateTimeOffset.UtcNow)).ToList();
        var proposal = _planning.AddReady(UserId, goals.Select(Proposal).ToArray());
        await _planning.MarkOfferedAsync(proposal.Id);
        AtStage(Stage.OfferPlan, GoalsDraft.Empty(null) with { ProposalId = proposal.Id });

        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "decision": "yes" }""",
            """{ "decision": "choose", "goalNumbers": [1, 2] }""",
            """
            { "decision": "adjust", "reply": "Então fica nos fins de semana.",
              "routines": [ { "activity": "ler", "frequency": "weekly", "daysOfWeek": [6, 7], "amount": 30, "unit": "páginas" } ] }
            """,
            """{ "decision": "confirm", "routines": [], "reply": null }""",
            """{ "decision": "skip", "routines": [], "reply": null }"""));

        // Yes → only 3 of the 4 goals are listed.
        var choices = await workflow.ReplyAsync(Message("sim"));
        Assert.Contains("**Ler 2 livros**", choices);
        Assert.Contains("**Aprender inglês**", choices);
        Assert.DoesNotContain("Meditar", choices);
        Assert.Contains("alguma dessas três", choices);
        Assert.Equal(Stage.ChoosePlanGoal, _store.States[UserId].Stage);

        // Two chosen → the first one in focus: suggestion first, then the question.
        var focus = await workflow.ReplyAsync(Message("leitura e academia"));
        Assert.Contains("Vamos começar por **Ler 2 livros**", focus);
        Assert.True(focus.IndexOf("Proposta para Ler 2 livros", StringComparison.Ordinal)
                    < focus.IndexOf("Como costuma ser Ler 2 livros?", StringComparison.Ordinal));
        Assert.Equal(Stage.RefinePlanGoal, _store.States[UserId].Stage);

        // The user's pattern → the routines are adapted and shown again for confirmation.
        var adjusted = await workflow.ReplyAsync(Message("prefiro ler mais nos fins de semana"));
        Assert.Contains("sábado e domingo", adjusted);
        Assert.Contains(PlanReview.ConfirmQuestion, adjusted);
        Assert.Empty(_goals.RoutinesReplaced);

        // Sim → saved as the goal's plan, and the next chosen goal comes into focus.
        var next = await workflow.ReplyAsync(Message("sim"));
        var (savedGoal, routines) = Assert.Single(_goals.RoutinesReplaced);
        Assert.Equal(goals[0].Id, savedGoal);
        Assert.Equal([6, 7], routines.Single().DaysOfWeek);
        Assert.Contains("Salvei o plano de **Ler 2 livros**", next);
        Assert.Contains("Proposta para Academia 3x por semana", next);
        Assert.Equal(GoalProposalStatus.Accepted, _planning.Proposals.Single().Goals[0].Status);

        // Skipped → back to the list, now with what is left.
        var again = await workflow.ReplyAsync(Message("pula essa"));
        Assert.Contains("**Aprender inglês**", again);
        Assert.Contains("**Meditar**", again);
        Assert.DoesNotContain("**Ler 2 livros**", again);
        Assert.Equal(GoalProposalStatus.Skipped, _planning.Proposals.Single().Goals[1].Status);
        Assert.Equal(Stage.ChoosePlanGoal, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task After_the_last_goal_pippin_shows_the_plan_and_closes_the_proposal()
    {
        var goal = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        var proposal = _planning.AddReady(UserId, Proposal(goal));
        AtStage(Stage.OfferPlan, GoalsDraft.Empty(null) with { ProposalId = proposal.Id });
        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "decision": "yes" }""",
            """{ "decision": "choose", "goalNumbers": [1] }""",
            """{ "decision": "confirm", "routines": [], "reply": null }"""));

        await workflow.ReplyAsync(Message("sim"));
        await workflow.ReplyAsync(Message("a de leitura"));
        var reply = await workflow.ReplyAsync(Message("fechado"));

        Assert.Contains("Seu planejamento", reply);
        Assert.Contains("ler — todo dia", reply);
        Assert.Equal(ProposalStatus.Closed, _planning.Proposals.Single().Status);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Talking_about_something_else_while_refining_saves_nothing()
    {
        var goal = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        var proposal = _planning.AddReady(UserId, Proposal(goal));
        AtStage(Stage.OfferPlan, GoalsDraft.Empty(null) with { ProposalId = proposal.Id });
        var workflow = CreateWorkflow(new FakeChatClient(
            """{ "decision": "yes" }""",
            """{ "decision": "choose", "goalNumbers": [1] }""",
            """{ "decision": "other", "routines": [], "reply": null }""",
            """{ "route": "out_of_scope", "reason": "previsão do tempo" }"""));

        await workflow.ReplyAsync(Message("sim"));
        await workflow.ReplyAsync(Message("leitura"));
        await workflow.ReplyAsync(Message("vai chover amanhã?"));

        Assert.Empty(_goals.RoutinesReplaced);
        Assert.Equal(Stage.RefinePlanGoal, _store.States[UserId].Stage);
        Assert.Equal(GoalProposalStatus.Open, _planning.Proposals.Single().Goals[0].Status);
    }

    [Fact]
    public async Task Not_now_keeps_the_proposal_for_later()
    {
        var goal = _goals.Add(UserId, "Ler 2 livros", DateTimeOffset.UtcNow);
        var proposal = _planning.AddReady(UserId, Proposal(goal));
        AtStage(Stage.OfferPlan, GoalsDraft.Empty(null) with { ProposalId = proposal.Id });

        var reply = await CreateWorkflow(new FakeChatClient("""{ "decision": "later" }""")).ReplyAsync(Message("agora não"));

        Assert.Contains("quero ver a proposta", reply);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.NotNull(await _planning.GetOpenAsync(UserId));
    }

    [Fact]
    public async Task A_stage_written_as_the_route_is_read_as_a_goto()
    {
        var goal = Goal("Ler mais");
        var context = new ConversationContext
        {
            UserId = UserId, DisplayName = "rafael", IncomingText = "quero detalhar a meta dos livros", History = [],
            Stage = Stage.CollectGoals, SavedGoals = [goal],
        };
        var classifier = new PathClassifier(new FakeChatClient(
            """{ "route": "DetailGoals", "stage": "DetailGoals", "goalNumber": 1, "reason": "detalhar" }"""));

        var decision = await classifier.ClassifyAsync(context, null, CancellationToken.None);

        Assert.Equal(PathKind.Goto, decision.Kind);
        Assert.Equal(Stage.DetailGoals, decision.Stage);
        Assert.Equal(goal.Id, decision.GoalId);
    }

    private const string Confirm ="""{ "decision": "confirm", "goal": null, "routines": [], "assumptions": [] }""";

    private const string TwoGoalProposal =
        """
        { "goals": [
            { "goalNumber": 2, "message": "Proposta da academia", "question": "Que dias?",
              "routines": [ { "activity": "ir à academia", "frequency": "weekly", "timesPerPeriod": 3 } ] },
            { "goalNumber": 1, "message": "Proposta da leitura", "question": "Lê de manhã ou à noite?",
              "routines": [ { "activity": "ler", "frequency": "daily", "amount": 10, "unit": "páginas" } ] }
        ] }
        """;

    private static GoalProposalInput Proposal(GoalDto goal) =>
        new(goal.Id, $"Proposta para {goal.Name}.", $"Como costuma ser {goal.Name}?",
            [new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.Pippin, Amount: 10, Unit: "páginas")]);

    private static GoalDto Goal(string name) =>
        new(Guid.CreateVersion7(), UserId, name, null, null, null, null, null, null, DateTimeOffset.UtcNow);

    private static GoalsDraft Detailing(GoalDto goal, bool proposeAfter) =>
        new(null, [GoalExtraction.FromSaved(goal)], [], [goal.Id],
            Question: "Qual o seu plano?",
            Plan: new PlanDraft([new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User)], RoutineSource.User),
            ProposeAfter: proposeAfter);

    private void AtStage(Stage stage, GoalsDraft? draft = null) =>
        _store.States[UserId] = new ConversationState(stage, "Rafael", draft);

    private PlanProposalsJob CreateJob(IChatClient ai, TimeProvider time) =>
        new(_planning, _goals, new PlanProposer(ai, time), CreateWorkflow(ai, time), time,
            NullLogger<PlanProposalsJob>.Instance);

    private PippinWorkflow CreateWorkflow(IChatClient ai, TimeProvider? time = null)
    {
        time ??= Noon;
        var progress = new FakeProgressService();
        var detailing = new GoalDetailing(ai, _goals, _planning, time);
        var review = new PlanReview(_planning, _goals);
        return new(
            _store,
            [
                new GreetingNode(),
                new AskNameNode(ai, time),
                new CollectGoalsNode(ai, time),
                new ConfirmGoalsNode(ai, _goals, detailing, time),
                new DetailGoalsNode(ai, detailing, time),
                new ConfirmCancelGoalNode(ai, _goals, detailing),
                new OfferPlanNode(ai, review),
                new ChoosePlanGoalNode(ai, review),
                new RefinePlanGoalNode(ai, review, detailing, _planning, time),
            ],
            new FallbackRouter(),
            new GreetingReply(),
            new PathClassifier(ai),
            new GoalsAgent(ai, _goals, progress, time, NullLoggerFactory.Instance),
            _goals,
            _channel,
            NullLogger<PippinWorkflow>.Instance);
    }

    private static PippinMessage Message(string text) => new(UserId, "rafael", text);
}
