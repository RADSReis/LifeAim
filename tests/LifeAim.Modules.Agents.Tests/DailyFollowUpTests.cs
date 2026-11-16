using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.FollowUp;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>Funcionalidade 3: Pippin asks about the planned activities at their moment and records the answers.</summary>
public class DailyFollowUpTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    // Monday, 16/11/2026, in São Paulo (UTC-3).
    private static readonly DateOnly Monday = new(2026, 11, 16);

    private const string Written = """{ "message": "Bora ler? 📚" }""";

    private readonly MovingTime _time = new(At(Monday, 8, 0));
    private readonly InMemoryConversationStore _store = new();
    private readonly FakeGoalService _goals = new() { RoutinesCreatedAt = At(new DateOnly(2026, 11, 10), 12, 0) };
    private readonly FakeProgressService _progress = new();
    private readonly FakePippinChannel _channel = new();
    private readonly FakeCheckInService _checkIns;

    public DailyFollowUpTests()
    {
        _checkIns = new FakeCheckInService(_time);
        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");
    }

    [Fact]
    public async Task A_morning_activity_is_asked_about_at_9_and_not_before()
    {
        var goal = await GoalWith(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User,
            DayPeriod: DayPeriod.Morning, Amount: 10, Unit: "páginas"));
        var ai = new FakeChatClient(Written);

        _time.Now = At(Monday, 8, 59);
        await Job(ai).RunAsync(CancellationToken.None);
        Assert.Empty(_channel.Sent);

        _time.Now = At(Monday, 9, 0);
        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Equal("Bora ler? 📚", Assert.Single(_channel.Sent).Text);
        var checkIn = Assert.Single(_checkIns.CheckIns);
        Assert.Equal((goal.Id, Monday, CheckInStatus.Asked), (checkIn.GoalId, checkIn.Day, checkIn.Status));
        var state = _store.States[UserId];
        Assert.Equal(Stage.CheckIn, state.Stage);
        var item = Assert.Single(state.Draft!.CheckIns!);
        Assert.Equal((checkIn.Id, "ler", 10m), (item.CheckInId, item.Activity, item.Amount));
        Assert.Equal("Bora ler? 📚", _store.Messages.Last().Turn.Content);
    }

    [Fact]
    public async Task Quiet_hours_are_respected_unless_the_user_said_that_exact_time()
    {
        await GoalWith(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, TimeOfDay: new TimeOnly(23, 0)));
        await GoalWith(new RoutineInput("meditar", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Night));

        _time.Now = At(Monday, 23, 5);
        await Job(new FakeChatClient(Written)).RunAsync(CancellationToken.None);

        Assert.Single(_channel.Sent);
        Assert.Equal("ler", Assert.Single(_checkIns.CheckIns).Activity); // "à noite" (21h) does not go past 22h
    }

    [Fact]
    public async Task Progress_already_reported_today_means_nothing_to_ask()
    {
        var goal = await GoalWith(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        await _progress.RecordAsync(new RecordProgressRequest(UserId, goal.Id, Monday, "leu 10 páginas", 10, "páginas"));

        _time.Now = At(Monday, 10, 0);
        await Job(new FakeChatClient(Written)).RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task A_user_in_the_middle_of_something_is_left_alone_without_spending_an_ai_call()
    {
        await GoalWith(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        _store.States[UserId] = new ConversationState(Stage.DetailGoals, "Rafael");
        var ai = new FakeChatClient(Written);

        _time.Now = At(Monday, 10, 0);
        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
        Assert.Empty(ai.Calls);
        Assert.Empty(_checkIns.CheckIns);
    }

    [Fact]
    public async Task Nothing_delivered_means_nothing_recorded_as_asked()
    {
        await GoalWith(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        _channel.Delivers = false;

        _time.Now = At(Monday, 10, 0);
        await Job(new FakeChatClient(Written)).RunAsync(CancellationToken.None);

        Assert.Empty(_checkIns.CheckIns);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task No_answer_is_asked_once_more_in_another_period_and_then_left()
    {
        await GoalWith(new RoutineInput("ir à academia", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        var ai = new FakeChatClient(Written);

        _time.Now = At(Monday, 9, 0);
        await Job(ai).RunAsync(CancellationToken.None);

        _time.Now = At(Monday, 12, 0); // 3 h: still waiting
        await Job(ai).RunAsync(CancellationToken.None);
        Assert.Single(_channel.Sent);

        _time.Now = At(Monday, 13, 0); // 4 h: the afternoon attempt
        await Job(ai).RunAsync(CancellationToken.None);
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(2, Assert.Single(_checkIns.CheckIns).TimesAsked);
        Assert.Equal(CheckInStatus.Asked, _checkIns.CheckIns.Single().Status); // not confirmed is not "not done"

        _time.Now = At(Monday, 19, 0);
        await Job(ai).RunAsync(CancellationToken.None);
        Assert.Equal(2, _channel.Sent.Count); // two per day at most
    }

    [Fact]
    public async Task An_unanswered_activity_rides_along_in_the_message_of_another_one()
    {
        var gym = await GoalWith(new RoutineInput("ir à academia", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        await GoalWith(new RoutineInput("meditar", RoutineFrequency.Daily, RoutineSource.User, TimeOfDay: new TimeOnly(12, 0)));
        var ai = new FakeChatClient(Written);

        _time.Now = At(Monday, 9, 0);
        await Job(ai).RunAsync(CancellationToken.None);
        _time.Now = At(Monday, 12, 0);
        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Equal(2, _channel.Sent.Count);
        var joined = Prompt(ai, 1).Split("\"semRespostaHoje\"")[1].Split("\"semConfirmacaoAntes\"")[0];
        Assert.Contains("\"atividade\": \"ir à academia\"", joined);
        Assert.Equal(2, _checkIns.CheckIns.Single(c => c.GoalId == gym.Id).TimesAsked);
        Assert.Equal(["ir à academia", "meditar"], _store.States[UserId].Draft!.CheckIns!.Select(i => i.Activity).Order());
    }

    [Fact]
    public async Task Yesterday_unconfirmed_is_asked_the_next_time_the_goal_is_planned()
    {
        var gym = await GoalWith(new RoutineInput("ir à academia", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        var routineId = (await _goals.ListRoutinesAsync(UserId)).Single().Id;
        var sunday = _checkIns.Add(UserId, gym.Id, routineId, Monday.AddDays(-1), "ir à academia", At(Monday.AddDays(-1), 9, 0), timesAsked: 2);
        var ai = new FakeChatClient(Written);

        _time.Now = At(Monday, 9, 0);
        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Contains("\"quando\": \"ontem\"", Prompt(ai, 0));
        Assert.Equal(3, _checkIns.CheckIns.Single(c => c.Id == sunday.Id).TimesAsked);
        Assert.Contains(_store.States[UserId].Draft!.CheckIns!, i => i.CheckInId == sunday.Id);
    }

    [Fact]
    public async Task The_message_knows_it_is_the_first_day()
    {
        await GoalWith(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        var ai = new FakeChatClient(Written);

        _time.Now = At(Monday, 9, 0);
        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Contains("\"primeiraVez\": true", Prompt(ai, 0));
    }

    [Fact]
    public async Task An_unreadable_ai_answer_still_sends_a_plain_message()
    {
        await GoalWith(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));

        _time.Now = At(Monday, 9, 0);
        await Job(new FakeChatClient("not json")).RunAsync(CancellationToken.None);

        Assert.Contains("Hora de ler", Assert.Single(_channel.Sent).Text);
    }

    [Theory]
    [InlineData(1, true)] // Monday
    [InlineData(2, false)] // Tuesday
    [InlineData(3, true)] // Wednesday
    public void Fixed_weekdays_follow_the_calendar(int dayOfMonthOffset, bool planned)
    {
        var routine = Routine(RoutineFrequency.Weekly, daysOfWeek: [1, 3, 5]);

        Assert.Equal(planned, FollowUpPlanner.IsPlannedOn(routine, Monday.AddDays(dayOfMonthOffset - 1), Monday.AddDays(-7), new HashSet<DateOnly>()));
    }

    [Fact]
    public void Three_times_a_week_is_asked_until_the_week_has_three_days_done()
    {
        var routine = Routine(RoutineFrequency.Weekly, timesPerPeriod: 3);
        var thursday = Monday.AddDays(3);

        Assert.True(FollowUpPlanner.IsPlannedOn(routine, thursday, Monday.AddDays(-7), new HashSet<DateOnly> { Monday, Monday.AddDays(1) }));
        Assert.False(FollowUpPlanner.IsPlannedOn(routine, thursday, Monday.AddDays(-7),
            new HashSet<DateOnly> { Monday, Monday.AddDays(1), Monday.AddDays(2) }));
        Assert.True(FollowUpPlanner.IsPlannedOn(routine, thursday.AddDays(4), Monday.AddDays(-7),
            new HashSet<DateOnly> { Monday, Monday.AddDays(1), Monday.AddDays(2) })); // a new week
    }

    [Fact]
    public void A_routine_is_not_planned_before_it_starts_or_after_it_ends()
    {
        var routine = Routine(RoutineFrequency.Daily) with { EndsOn = Monday };

        Assert.False(FollowUpPlanner.IsPlannedOn(routine, Monday.AddDays(-1), Monday, new HashSet<DateOnly>()));
        Assert.True(FollowUpPlanner.IsPlannedOn(routine, Monday, Monday, new HashSet<DateOnly>()));
        Assert.False(FollowUpPlanner.IsPlannedOn(routine, Monday.AddDays(1), Monday, new HashSet<DateOnly>()));
    }

    [Fact]
    public async Task Answering_done_records_the_progress_and_closes_the_follow_up()
    {
        var goal = await AskedAbout(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User,
            DayPeriod: DayPeriod.Morning, Amount: 10, Unit: "páginas"));
        var workflow = Workflow(new FakeChatClient(
            """
            { "decision": "report", "items": [ { "number": 1, "outcome": "done", "amount": 15, "unit": "páginas", "note": "leu 15 páginas" } ],
              "reply": "Que demais! 15 páginas!" }
            """));

        var reply = await workflow.ReplyAsync(Message("li 15 páginas"));

        Assert.Equal("Que demais! 15 páginas!", reply);
        var entry = Assert.Single(_progress.Entries);
        Assert.Equal((goal.Id, Monday, 15m, "páginas", "leu 15 páginas"), (entry.GoalId, entry.OccurredOn, entry.Amount, entry.Unit, entry.Note));
        Assert.Equal(CheckInStatus.Done, _checkIns.CheckIns.Single().Status);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Null(_store.States[UserId].Draft!.CheckIns);
    }

    [Fact]
    public async Task Answering_not_done_keeps_the_reason_and_records_no_progress()
    {
        await AskedAbout(new RoutineInput("ir à academia", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        var workflow = Workflow(new FakeChatClient(
            """
            { "decision": "report", "items": [ { "number": 1, "outcome": "not_done", "reason": "fiquei preso no trabalho" } ],
              "reply": "Tudo bem, imprevistos acontecem. Amanhã a gente retoma!" }
            """));

        await workflow.ReplyAsync(Message("não deu, fiquei preso no trabalho"));

        Assert.Empty(_progress.Entries);
        var checkIn = _checkIns.CheckIns.Single();
        Assert.Equal((CheckInStatus.Missed, "fiquei preso no trabalho"), (checkIn.Status, checkIn.Reason));
    }

    [Fact]
    public async Task An_activity_not_mentioned_stays_open_and_the_follow_up_waits_for_it()
    {
        await GoalWith(new RoutineInput("ir à academia", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        await GoalWith(new RoutineInput("meditar", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        _time.Now = At(Monday, 9, 0);
        await Job(new FakeChatClient(Written)).RunAsync(CancellationToken.None);
        var meditar = _store.States[UserId].Draft!.CheckIns!.Single(i => i.Activity == "meditar");
        var workflow = Workflow(new FakeChatClient(
            """
            { "decision": "report", "items": [ { "number": 1, "outcome": "done", "note": "foi à academia" } ],
              "reply": "Boa! E a meditação, rolou?" }
            """));

        await workflow.ReplyAsync(Message("fui na academia"));

        var state = _store.States[UserId];
        Assert.Equal(Stage.CheckIn, state.Stage);
        Assert.Equal(meditar.CheckInId, Assert.Single(state.Draft!.CheckIns!).CheckInId);
        Assert.Equal(CheckInStatus.Asked, _checkIns.CheckIns.Single(c => c.Id == meditar.CheckInId).Status);

        var pending = new CheckInNode(new FakeChatClient(""), _progress, _checkIns, _time).PendingQuestion(new ConversationContext
        {
            UserId = UserId, DisplayName = "rafael", IncomingText = "", History = [], Stage = Stage.CheckIn, Draft = state.Draft,
        });
        Assert.Equal("Me conta: conseguiu meditar hoje?", pending);
    }

    [Fact]
    public async Task A_message_about_something_else_leaves_the_activities_open()
    {
        await AskedAbout(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        var workflow = Workflow(new FakeChatClient(
            """{ "decision": "other", "items": [], "reply": null }""",
            """{ "route": "out_of_scope", "reason": "previsão do tempo" }"""));

        await workflow.ReplyAsync(Message("vai chover hoje?"));

        Assert.Equal(CheckInStatus.Asked, _checkIns.CheckIns.Single().Status);
        Assert.Empty(_progress.Entries);
        Assert.Equal(Stage.CheckIn, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Reporting_progress_to_the_goals_agent_answers_the_follow_up_of_that_day()
    {
        var goal = await AskedAbout(new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, DayPeriod: DayPeriod.Morning));
        var context = new ConversationContext
        {
            UserId = UserId, DisplayName = "rafael", IncomingText = "li 10 páginas", History = [], Stage = Stage.CollectGoals,
            SavedGoals = await _goals.ListAsync(UserId),
        };
        var tools = new GoalTools(context, _goals, _progress, _checkIns, _time);

        await tools.RecordProgressAsync(1, "leu 10 páginas", 10, "páginas");

        Assert.Equal(CheckInStatus.Done, _checkIns.CheckIns.Single(c => c.GoalId == goal.Id).Status);
    }

    private async Task<GoalDto> GoalWith(RoutineInput routine)
    {
        var goal = _goals.Add(UserId, $"Meta de {routine.Activity}", At(new DateOnly(2026, 11, 10), 12, 0));
        await _goals.ReplaceRoutinesAsync(goal.Id, UserId, [routine]);
        return goal;
    }

    /// <summary>A goal whose activity Pippin just asked about (the job ran at 9h): the conversation is at CheckIn.</summary>
    private async Task<GoalDto> AskedAbout(RoutineInput routine)
    {
        var goal = await GoalWith(routine);
        _time.Now = At(Monday, 9, 0);
        await Job(new FakeChatClient(Written)).RunAsync(CancellationToken.None);
        Assert.Equal(Stage.CheckIn, _store.States[UserId].Stage);
        return goal;
    }

    private DailyFollowUpJob Job(IChatClient ai) =>
        new(_goals, _progress, _checkIns, _store, new CheckInWriter(ai), Workflow(ai), _time, NullLogger<DailyFollowUpJob>.Instance);

    private PippinWorkflow Workflow(IChatClient ai) =>
        new(
            _store,
            [new GreetingNode(), new CollectGoalsNode(ai, _time), new CheckInNode(ai, _progress, _checkIns, _time)],
            new FallbackRouter(),
            new GreetingReply(),
            new PathClassifier(ai),
            new GoalsAgent(ai, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance),
            _goals,
            _channel,
            NullLogger<PippinWorkflow>.Instance);

    private static string Prompt(FakeChatClient ai, int call) => ai.Calls[call][0].Text;

    private static RoutineDto Routine(RoutineFrequency frequency, IReadOnlyList<int>? daysOfWeek = null, int? timesPerPeriod = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "ler", frequency, 1, daysOfWeek ?? [], timesPerPeriod, null, null,
            null, null, null, RoutineSource.User, null, null, DateTimeOffset.UtcNow);

    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(-3));

    private static PippinMessage Message(string text) => new(UserId, "rafael", text);

    private sealed class MovingTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
