using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.Fitness;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Fitness.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>
/// The journal's reminders: Pippin takes the initiative at the time the user chose, once a day, only while what the
/// reminder asks is still missing, and never on top of a cadastro in progress.
/// </summary>
public class FitnessReminderTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 7, 13);

    private readonly MovingTime _time = new(At(Today, 8, 0));
    private readonly InMemoryConversationStore _store = new();
    private readonly FakeGoalService _goals = new();
    private readonly FakeProgressService _progress = new();
    private readonly FakeCheckInService _checkIns = new();
    private readonly FakeFitnessJournal _journal = new();
    private readonly FakeFitnessReminders _reminders = new();
    private readonly FakePippinChannel _channel = new();

    private readonly Goals.Contracts.GoalDto _weight;

    public FitnessReminderTests()
    {
        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");
        _weight = _goals.Add(UserId, "Chegar a menos de 84 kg", DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task With_the_goal_paused_the_reminders_stop_and_come_back_with_it()
    {
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.WeighIn, new TimeOnly(8, 0)));
        var job = Job(new FakeChatClient("not used"));
        _time.Now = At(Today, 8, 5);

        await _goals.PauseAsync(_weight.Id, UserId, null, "viagem");
        Assert.False(await job.RemindAsync(UserId, CancellationToken.None));

        await _goals.ResumeAsync(_weight.Id, UserId);
        Assert.True(await job.RemindAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task The_weigh_in_reminder_goes_out_at_its_time_once_and_the_stage_stays()
    {
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.WeighIn, new TimeOnly(8, 0)));
        var job = Job(new FakeChatClient("not used"));

        _time.Now = At(Today, 7, 55);
        Assert.False(await job.RemindAsync(UserId, CancellationToken.None)); // not yet

        _time.Now = At(Today, 8, 2);
        Assert.True(await job.RemindAsync(UserId, CancellationToken.None));
        Assert.False(await job.RemindAsync(UserId, CancellationToken.None)); // once a day

        Assert.Equal([(UserId, FitnessReminderWriter.WeighIn)], _channel.Sent);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Equal(FitnessReminderWriter.WeighIn, _store.Messages.Single().Turn.Content); // part of the history

        _time.Now = At(Today.AddDays(1), 8, 2);
        Assert.True(await job.RemindAsync(UserId, CancellationToken.None)); // and again the next day
    }

    [Fact]
    public async Task Nothing_is_sent_when_what_the_reminder_asks_is_already_there()
    {
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.WeighIn, new TimeOnly(8, 0)));
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.NoRecord, new TimeOnly(14, 0)));
        await _journal.RecordBodyAsync(new RecordBodyRequest(UserId, Today, WeightKg: 94.5m));
        await _journal.AddMealAsync(new AddMealRequest(UserId, Today, new TimeOnly(10, 0), "Café da manhã", [Item(400, 30)]));

        _time.Now = At(Today, 14, 5);

        Assert.False(await Job(new FakeChatClient("not used")).RemindAsync(UserId, CancellationToken.None));
        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task A_reminder_too_late_is_dropped_for_the_day()
    {
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.WeighIn, new TimeOnly(8, 0)));

        _time.Now = At(Today, 11, 30); // more than 3 hours after

        Assert.False(await Job(new FakeChatClient("not used")).RemindAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task A_cadastro_in_progress_is_not_interrupted_and_the_reminder_waits()
    {
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.WeighIn, new TimeOnly(8, 0)));
        _store.States[UserId] = new ConversationState(Stage.ConfirmGoals, "Rafael");
        var job = Job(new FakeChatClient("not used"));

        _time.Now = At(Today, 8, 5);
        Assert.False(await job.RemindAsync(UserId, CancellationToken.None));

        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");
        Assert.True(await job.RemindAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task A_message_that_could_not_be_delivered_is_not_marked_as_sent()
    {
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.WeighIn, new TimeOnly(8, 0)));
        _channel.Delivers = false;
        _time.Now = At(Today, 8, 5);

        Assert.False(await Job(new FakeChatClient("not used")).RemindAsync(UserId, CancellationToken.None));
        Assert.Empty(_reminders.Sent);
    }

    [Fact]
    public async Task The_summary_of_the_day_is_written_from_the_totals_the_code_added_up()
    {
        await _journal.SaveProfileAsync(new SaveProfileRequest(UserId, DailyEnergyKcal: 2350));
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.DayClose, new TimeOnly(21, 30)));
        await _journal.AddMealAsync(new AddMealRequest(UserId, Today, new TimeOnly(13, 0), "Almoço", [Item(1800, 140)]));
        var ai = new FakeChatClient("**Fechamento do dia** — 13/07\n- **Ingerido:** 1.800 kcal");

        _time.Now = At(Today, 21, 31);
        Assert.True(await Job(ai).RemindAsync(UserId, CancellationToken.None));

        Assert.StartsWith("**Fechamento do dia**", _channel.Sent.Single().Text);
        var prompt = ai.Calls.Single()[0].Text;
        Assert.Contains("Totais do dia: ingerido 1.800 kcal · proteína 140 g", prompt);
        Assert.Contains("se o dia terminasse agora: déficit de 550 kcal", prompt);
    }

    [Fact]
    public async Task There_is_no_summary_of_a_day_with_nothing_recorded()
    {
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.DayClose, new TimeOnly(21, 30)));
        _time.Now = At(Today, 21, 31);

        Assert.False(await Job(new FakeChatClient("not used")).RemindAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task A_free_reminder_said_to_be_done_is_not_sent_that_day()
    {
        var tools = Tools("me lembra da vitamina às 9h");
        Assert.StartsWith("Lembrete ligado: \"tomar a vitamina\" às 09:00", await tools.SetReminderAsync("custom", "09:00", "tomar a vitamina"));

        Assert.StartsWith("Feito hoje", await tools.CompleteReminderAsync("Tomar a vitamina"));

        _time.Now = At(Today, 9, 1);
        var job = Job(new FakeChatClient("not used"));
        Assert.False(await job.RemindAsync(UserId, CancellationToken.None));

        _time.Now = At(Today.AddDays(1), 9, 1);
        Assert.True(await job.RemindAsync(UserId, CancellationToken.None));
        Assert.Equal(FitnessReminderWriter.Custom("tomar a vitamina"), _channel.Sent.Single().Text);
    }

    [Fact]
    public async Task Reminders_are_switched_on_changed_and_off_by_conversation()
    {
        var tools = Tools("me lembra de pesar");

        Assert.Contains("pesagem às 09:00", await tools.SetReminderAsync("weigh_in")); // the default time
        Assert.Contains("pesagem às 07:30", await tools.SetReminderAsync("weigh_in", "07:30"));
        Assert.Contains("fechamento do dia às 21:30", await tools.SetReminderAsync("day_close"));
        Assert.Equal(2, _reminders.Reminders.Count); // the weigh-in changed, it was not duplicated

        Assert.StartsWith("Erro:", await tools.SetReminderAsync("custom", null, "tomar a vitamina")); // a free one needs its time
        Assert.StartsWith("Erro:", await tools.SetReminderAsync("almoço"));
        Assert.StartsWith("Erro:", await tools.CompleteReminderAsync("tomar a vitamina"));

        Assert.Equal("Lembrete desligado.", await tools.RemoveReminderAsync("weigh_in"));
        Assert.StartsWith("Erro:", await tools.RemoveReminderAsync("weigh_in"));
        Assert.Equal(ReminderKind.DayClose, Assert.Single(_reminders.Reminders).Reminder.Kind);
    }

    [Fact]
    public async Task The_prompt_tells_the_agent_which_reminders_are_on()
    {
        await _reminders.SetAsync(new SetReminderRequest(UserId, ReminderKind.WeighIn, new TimeOnly(8, 0)));
        var ai = new FakeChatClient(FakeChatClient.Text("ok"));

        await new FitnessAgent(ai, _journal, _reminders, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance)
            .ReplyAsync(Context("quais lembretes eu tenho?"), CancellationToken.None);

        Assert.Contains("- pesagem às 08:00", ai.Calls[0][0].Text);
    }

    [Fact]
    public void A_time_after_midnight_belongs_to_the_food_day_before()
    {
        var late = new ReminderDto(Guid.CreateVersion7(), ReminderKind.Custom, new TimeOnly(0, 30), "tomar o remédio", false, false);
        var day = new FitnessDayDto(Today, null, [], [], []);

        Assert.Empty(FitnessReminderPlanner.Due(At(Today, 23, 50).DateTime, [late], day));
        Assert.Single(FitnessReminderPlanner.Due(At(Today.AddDays(1), 0, 35).DateTime, [late], day)); // still the 13th's food day
        Assert.Empty(FitnessReminderPlanner.Due(At(Today, 0, 35).DateTime, [late with { SentToday = true }], day));
    }

    private static MealItemDto Item(decimal kcal, decimal protein) =>
        new("Prato", null, null, kcal, kcal, protein, protein, null, null, NutritionSource.Estimate);

    private FitnessTools Tools(string message) => new(Context(message), _journal, _reminders, _goals, _progress, _checkIns, _time);

    private ConversationContext Context(string message) => new()
    {
        UserId = UserId,
        DisplayName = "rafael",
        IncomingText = message,
        History = [],
        Stage = Stage.CollectGoals,
        UserName = "Rafael",
    };

    private FitnessReminderJob Job(IChatClient ai)
    {
        var agent = new FitnessAgent(ai, _journal, _reminders, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance);
        var workflow = new PippinWorkflow(
            _store,
            [new GreetingNode(), new CollectGoalsNode(ai, _time)],
            new FallbackRouter(),
            new GreetingReply(),
            new PathClassifier(ai),
            new GoalsAgent(ai, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance),
            new FitnessAgent(ai, _journal, _reminders, _goals, _progress, _checkIns, _time, NullLoggerFactory.Instance),
            _goals,
            _channel,
            NullLogger<PippinWorkflow>.Instance);
        return new FitnessReminderJob(
            _reminders, _journal, _store, new FitnessReminderWriter(ai), agent, workflow, _time, NullLogger<FitnessReminderJob>.Instance);
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, TimeSpan.FromHours(-3));

    private sealed class MovingTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
