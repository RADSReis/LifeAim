using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.Fitness;
using LifeAim.Modules.Agents.Application.Balance;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>Funcionalidade 5: priorities and conflicts between goals, started from the history and talked with the user.</summary>
public class GoalBalanceTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 11, 17);

    private const string Written = """{ "message": "Você está arrasando na **Academia**! Não vamos mexer nisso. A **leitura** ainda está mais difícil." }""";

    private const string Explains =
        """
        { "decision": "explains", "obstacle": "chego cansado à noite",
          "routines": [ { "activity": "ler", "frequency": "daily", "dayPeriod": "morning", "amount": 5, "unit": "páginas" } ],
          "reply": "Faz sentido! Que tal 5 páginas de manhã, logo depois do café?" }
        """;

    private readonly MovingTime _time = new(At(Today, 15, 0));
    private readonly InMemoryConversationStore _store = new();
    private readonly FakeGoalService _goals = new();
    private readonly FakeProgressService _progress = new();
    private readonly FakePippinChannel _channel = new();
    private readonly FakeCheckInService _checkIns;
    private readonly FakeBalanceService _balance;
    private readonly GoalDto _gym;
    private readonly GoalDto _reading;

    public GoalBalanceTests()
    {
        _checkIns = new FakeCheckInService(_time);
        _balance = new FakeBalanceService(_time);
        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");
        _gym = _goals.Add(UserId, "Academia", DateTimeOffset.UtcNow);
        _reading = _goals.Add(UserId, "Ler 12 livros", DateTimeOffset.UtcNow);
        _goals.ReplaceRoutinesAsync(_gym.Id, UserId, [new RoutineInput("ir à academia", RoutineFrequency.Daily, RoutineSource.User)]).Wait();
        _goals.ReplaceRoutinesAsync(_reading.Id, UserId,
            [new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Night, Amount: 20, Unit: "páginas")]).Wait();
        _balance.Balance = Balance(focus: _reading.Id);
    }

    [Fact]
    public async Task A_signal_starts_one_review_that_praises_and_protects_what_works()
    {
        var ai = new FakeChatClient(Written);

        Assert.True(await Job(ai).ReviewAsync(UserId, CancellationToken.None));

        var text = Assert.Single(_channel.Sent).Text;
        Assert.StartsWith("Você está arrasando na **Academia**!", text);
        Assert.EndsWith(BalanceWriter.Question(_reading.Name), text);
        var review = Assert.Single(_balance.Reviews);
        Assert.Equal((_reading.Id, ReviewStatus.Offered), (review.FocusGoalId, review.Status));
        Assert.Equal([_gym.Id], review.ProtectedGoalIds);
        Assert.Equal([BalanceSignal.RecurringDifficulty], review.Signals);
        Assert.Equal(Stage.BalanceReview, _store.States[UserId].Stage);
        Assert.Equal(review.Id, _store.States[UserId].Draft!.BalanceReviewId);

        // The facts go to the AI already counted; the rules of the page go with them.
        var prompt = ai.Calls[0][0].Text;
        Assert.Contains("funcionando bem (proteger)", prompt);
        Assert.Contains("Nunca compare os percentuais", prompt);
        Assert.Contains("estimativa", prompt);
    }

    [Fact]
    public async Task Not_again_within_two_weeks()
    {
        var ai = new FakeChatClient(Written);
        await Job(ai).ReviewAsync(UserId, CancellationToken.None);
        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");

        _time.Now = At(Today.AddDays(13), 15, 0);
        Assert.False(await Job(ai).ReviewAsync(UserId, CancellationToken.None));

        _time.Now = At(Today.AddDays(15), 15, 0);
        Assert.True(await Job(ai).ReviewAsync(UserId, CancellationToken.None));
        Assert.Equal(2, _channel.Sent.Count);
    }

    [Fact]
    public async Task Without_a_signal_nothing_is_said()
    {
        _balance.Balance = Balance(focus: null);
        var ai = new FakeChatClient(Written);

        Assert.False(await Job(ai).ReviewAsync(UserId, CancellationToken.None));

        Assert.Empty(_channel.Sent);
        Assert.Empty(ai.Calls);
        Assert.Empty(_balance.Reviews);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(20)]
    [InlineData(22)]
    public async Task Only_during_the_day(int hour)
    {
        _time.Now = At(Today, hour, 30);

        await Job(new FakeChatClient(Written)).RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
        Assert.Empty(_balance.Analyzed);
    }

    [Theory]
    [InlineData(nameof(Stage.CheckIn))]
    [InlineData(nameof(Stage.DetailGoals))]
    [InlineData(nameof(Stage.SummaryInsight))]
    public async Task Never_on_top_of_something_in_progress(string stage)
    {
        _store.States[UserId] = new ConversationState(Enum.Parse<Stage>(stage), "Rafael");
        var ai = new FakeChatClient(Written);

        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
        Assert.Empty(ai.Calls);
    }

    [Fact]
    public async Task Nothing_delivered_means_nothing_recorded()
    {
        _channel.Delivers = false;

        await Job(new FakeChatClient(Written)).RunAsync(CancellationToken.None);

        Assert.Empty(_balance.Reviews);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task After_a_no_about_the_same_goal_it_waits_twice_as_long()
    {
        _balance.Reviews.Add(new BalanceReviewDto(
            Guid.CreateVersion7(), UserId, _reading.Id, [BalanceSignal.RecurringDifficulty], [], "antes", ReviewStatus.Declined,
            null, _time.Now.AddDays(-20)));

        Assert.False(await Job(new FakeChatClient(Written)).ReviewAsync(UserId, CancellationToken.None));

        _balance.Reviews[0] = _balance.Reviews[0] with { FocusGoalId = _gym.Id };
        Assert.True(await Job(new FakeChatClient(Written)).ReviewAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_ai_answer_still_sends_a_plain_message_that_protects_the_habit()
    {
        await Job(new FakeChatClient("not json")).ReviewAsync(UserId, CancellationToken.None);

        var text = Assert.Single(_channel.Sent).Text;
        Assert.Contains("Você está indo muito bem em **Academia**", text);
        Assert.Contains("não vamos mexer no que está funcionando", text);
        Assert.Contains("já fez 120 páginas", text);
    }

    [Fact]
    public async Task The_user_explains_Pippin_proposes_a_small_step_and_yes_changes_only_that_goal()
    {
        await Job(new FakeChatClient(Written)).ReviewAsync(UserId, CancellationToken.None);
        var gymPlans = _goals.RoutinesReplaced.Count(r => r.GoalId == _gym.Id);

        var proposal = await Workflow(new FakeChatClient(Explains)).ReplyAsync(Message("chego muito cansado à noite"));

        Assert.StartsWith("Faz sentido! Que tal 5 páginas de manhã", proposal);
        Assert.Contains("5 páginas", proposal);
        Assert.EndsWith(BalanceReviewNode.ConfirmQuestion, proposal);
        var review = _balance.Reviews.Single();
        Assert.Equal((ReviewStatus.Explained, "chego cansado à noite"), (review.Status, review.Obstacle));
        Assert.Equal(Stage.BalanceReview, _store.States[UserId].Stage);
        Assert.Equal(20m, _reading.RoutinesFor(_goals).Single().Amount); // nothing saved before the yes

        var saved = await Workflow(new FakeChatClient("""{ "decision": "confirm" }""")).ReplyAsync(Message("bora"));

        Assert.StartsWith("Ajustei o plano de **Ler 12 livros**!", saved);
        var routine = _reading.RoutinesFor(_goals).Single();
        Assert.Equal((5m, DayPeriod.Morning, RoutineSource.Pippin), (routine.Amount, routine.DayPeriod, routine.Source));
        Assert.Equal(gymPlans, _goals.RoutinesReplaced.Count(r => r.GoalId == _gym.Id)); // the habit that works is untouched
        Assert.Equal(ReviewStatus.Accepted, _balance.Reviews.Single().Status);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Null(_store.States[UserId].Draft!.BalanceReviewId);
    }

    [Fact]
    public async Task Prompts_show_the_protected_habits_as_off_limits()
    {
        await Job(new FakeChatClient(Written)).ReviewAsync(UserId, CancellationToken.None);
        var ai = new FakeChatClient(Explains);

        await Workflow(ai).ReplyAsync(Message("chego cansado"));

        var prompt = ai.Calls[0][0].Text;
        Assert.Contains("NÃO podem ser reduzidos", prompt);
        Assert.Contains("Academia", prompt);
        Assert.Contains("não presuma a causa", prompt);
    }

    [Fact]
    public async Task Keeping_the_plan_is_respected()
    {
        await Job(new FakeChatClient(Written)).ReviewAsync(UserId, CancellationToken.None);
        var plans = _goals.RoutinesReplaced.Count;

        var reply = await Workflow(new FakeChatClient("""{ "decision": "keep", "reply": null }""")).ReplyAsync(Message("prefiro manter"));

        Assert.StartsWith("Tudo bem! Seguimos com o plano de **Ler 12 livros**", reply);
        Assert.Equal(ReviewStatus.Declined, _balance.Reviews.Single().Status);
        Assert.Equal(plans, _goals.RoutinesReplaced.Count);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task A_temporary_obstacle_is_welcomed_without_changing_the_plan()
    {
        await Job(new FakeChatClient(Written)).ReviewAsync(UserId, CancellationToken.None);
        var plans = _goals.RoutinesReplaced.Count;

        var reply = await Workflow(new FakeChatClient(
                """{ "decision": "explains", "obstacle": "estive viajando", "routines": [], "reply": "Viagem mexe mesmo com a rotina! Na volta a gente retoma." }"""))
            .ReplyAsync(Message("estive viajando"));

        Assert.Equal("Viagem mexe mesmo com a rotina! Na volta a gente retoma.", reply);
        Assert.Equal((ReviewStatus.Explained, "estive viajando"), (_balance.Reviews.Single().Status, _balance.Reviews.Single().Obstacle));
        Assert.Equal(plans, _goals.RoutinesReplaced.Count);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task A_follow_up_replaces_a_review_nobody_answered()
    {
        await Job(new FakeChatClient(Written)).ReviewAsync(UserId, CancellationToken.None);

        var started = await Workflow(new FakeChatClient(Written)).StartCheckInAsync(
            UserId, "Bora treinar hoje?", _ => Task.FromResult<IReadOnlyList<CheckInItem>>([]), CancellationToken.None);

        Assert.True(started);
        var state = _store.States[UserId];
        Assert.Equal(Stage.CheckIn, state.Stage);
        Assert.Null(state.Draft!.BalanceReviewId);
        Assert.Null(state.Draft.Question);
        Assert.Equal(ReviewStatus.Offered, _balance.Reviews.Single().Status); // never answered, never insisted on
    }

    private BalanceDto Balance(Guid? focus)
    {
        var gym = new GoalBalanceDto(_gym.Id, _gym.Name, 10, 1, 0, 91, 90, 92, GoalHealth.Thriving, [], null, null, null);
        var reading = new GoalBalanceDto(_reading.Id, _reading.Name, 2, 5, 3, 29, 20, 40, GoalHealth.Struggling,
            ["cansado"], "páginas", 120, null);
        return new BalanceDto(
            Today.AddDays(-27), Today, [gym, reading],
            focus is null ? [] : [new BalanceSignalDto(BalanceSignal.RecurringDifficulty, [_reading.Id])], focus);
    }

    private GoalBalanceJob Job(IChatClient ai) =>
        new(_goals, _balance, _store, new BalanceWriter(ai), Workflow(ai), _time, NullLogger<GoalBalanceJob>.Instance);

    private PippinWorkflow Workflow(IChatClient ai) =>
        new(
            _store,
            [
                new GreetingNode(), new CollectGoalsNode(ai, _time), new CheckInNode(ai, _progress, _checkIns, _time),
                new BalanceReviewNode(ai, _balance, _goals, _time),
            ],
            new FallbackRouter(),
            new GreetingReply(),
            new PathClassifier(ai),
            new GoalsAgent(ai, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance),
            new FitnessAgent(ai, new FakeFitnessJournal(), new FakeFitnessReminders(), _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance),
            _goals,
            _channel,
            NullLogger<PippinWorkflow>.Instance);

    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(-3));

    private static PippinMessage Message(string text) => new(UserId, "rafael", text);

    private sealed class MovingTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}

internal static class GoalRoutines
{
    /// <summary>The goal's plan as it is now in the fake Goals module.</summary>
    public static IReadOnlyList<RoutineDto> RoutinesFor(this GoalDto goal, FakeGoalService goals) =>
        goals.ListRoutinesAsync(goal.UserId).Result.Where(r => r.GoalId == goal.Id).ToList();
}
