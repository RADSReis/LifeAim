using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.Fitness;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Reports;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>Funcionalidade 4: the monthly summary, the quarterly panorama and the optional insight.</summary>
public class MonthlySummaryTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateOnly November = new(2026, 11, 1);

    private const string Summary = """{ "message": "Você fez 80% do que foi proposto em novembro! 💪", "insightTeaser": null, "insight": null }""";

    private const string WithInsight =
        """
        { "message": "Você fez 80% do que foi proposto em novembro! 💪",
          "insightTeaser": "Tive um insight sobre seu hábito de leitura. Quer saber qual?",
          "insight": "Você lê 3 vezes mais nos fins de semana." }
        """;

    private readonly MovingTime _time = new(At(new DateOnly(2026, 12, 1), 10, 0));
    private readonly InMemoryConversationStore _store = new();
    private readonly FakeGoalService _goals = new();
    private readonly FakeProgressService _progress = new();
    private readonly FakePippinChannel _channel = new();
    private readonly FakeReportingService _reporting = new();
    private readonly FakeCheckInService _checkIns;

    public MonthlySummaryTests()
    {
        _checkIns = new FakeCheckInService(_time);
        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");
        var goal = _goals.Add(UserId, "Ler 12 livros", DateTimeOffset.UtcNow);
        _goals.ReplaceRoutinesAsync(goal.Id, UserId, [new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User)]).Wait();
        _reporting.Report = Report(done: 12, missed: 3);
    }

    [Fact]
    public async Task On_the_first_of_the_month_the_month_that_ended_is_summarized_once()
    {
        var ai = new FakeChatClient(Summary);

        await Job(ai).RunAsync(CancellationToken.None);
        _time.Now = At(new DateOnly(2026, 12, 1), 15, 0);
        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Equal("Você fez 80% do que foi proposto em novembro! 💪", Assert.Single(_channel.Sent).Text);
        var summary = Assert.Single(_reporting.Summaries);
        Assert.Equal((November, SummaryStatus.Sent, InsightStatus.None), (summary.Month, summary.Status, summary.InsightStatus));
        Assert.Equal([November], _reporting.Built);
        Assert.Single(ai.Calls);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Equal(summary.Message, _store.Messages.Last().Turn.Content);
    }

    [Fact]
    public async Task The_month_of_the_weight_loss_journal_goes_into_the_summary_already_counted()
    {
        for (var day = 1; day <= 20; day++)
        {
            var date = new DateOnly(2026, 11, day);
            await _journal.RecordBodyAsync(new Fitness.Contracts.RecordBodyRequest(UserId, date, WeightKg: 95m - day * 0.1m));
            await _journal.AddMealAsync(new Fitness.Contracts.AddMealRequest(UserId, date, new TimeOnly(13, 0), "Almoço",
                [new Fitness.Contracts.MealItemDto("Prato", null, null, 1900, 2100, 140, 150, null, null, Fitness.Contracts.NutritionSource.Estimate)]));
        }

        await _journal.SaveProfileAsync(new Fitness.Contracts.SaveProfileRequest(UserId, DailyEnergyKcal: 2350, TargetWeightKg: 84, ProteinTargetG: 150));
        var ai = new FakeChatClient(Summary);

        await Job(ai).RunAsync(CancellationToken.None);

        var prompt = ai.Calls.Single()[0].Text;
        Assert.Contains("- Dias com algum registro no diário: 20.", prompt);
        Assert.Contains("- Alimentação registrada em 20 dia(s): média de ~1.900–2.100 kcal e proteína ~140–150 g por dia.", prompt);
        Assert.Contains("- Desses dias, 20 fecharam em déficit, 0 em superávit e 0 perto da manutenção", prompt);
        Assert.Contains("- Meta de proteína (150 g) alcançada em 20 desses dias.", prompt);
        Assert.Contains("- Peso: 20 pesagens. Média das 7 primeiras: 94,6 kg; das 7 últimas: 93,3 kg (-1,3 kg). Menor do período: 93 kg. Peso alvo: 84 kg.", prompt);
    }

    [Fact]
    public void A_period_with_almost_nothing_recorded_is_not_summarized()
    {
        var day = new Fitness.Contracts.FitnessDayDto(new DateOnly(2026, 11, 3), null, [], [], ["viagem"]);

        Assert.Null(Application.Fitness.FitnessPeriod.Summary([day, day with { Day = day.Day.AddDays(1) }], null));
    }

    [Fact]
    public async Task A_weight_reported_many_times_is_shown_as_first_last_and_change_never_as_a_sum()
    {
        var goal = _goals.Add(UserId, "Chegar a 84 kg", DateTimeOffset.UtcNow);
        var progress = new FakeProgressService();
        await progress.RecordAsync(new Progress.Contracts.RecordProgressRequest(UserId, goal.Id, new DateOnly(2026, 11, 2), "pesou 95 kg", 95, "kg"));
        await progress.RecordAsync(new Progress.Contracts.RecordProgressRequest(UserId, goal.Id, new DateOnly(2026, 11, 20), "pesou 93,6 kg", 93.6m, "kg"));
        var context = new ConversationContext
        {
            UserId = UserId, DisplayName = "rafael", IncomingText = "como estou?", History = [], Stage = Stage.CollectGoals, SavedGoals = _goals.Saved,
        };

        var text = await new Application.Agent.GoalTools(context, _goals, progress, _checkIns, _time).GetGoalAsync(2, CancellationToken.None);

        Assert.Contains("Em kg: primeiro registro 95 em 02/11/2026, último 93,6 em 20/11/2026 (variação: -1,4)", text);
        Assert.DoesNotContain("Total em kg", text);
    }

    [Theory]
    [InlineData(1, 9)] // before 10h
    [InlineData(8, 12)] // after the first week: no longer news
    [InlineData(15, 12)]
    public async Task Outside_the_first_week_from_10h_nothing_is_sent(int day, int hour)
    {
        _time.Now = At(new DateOnly(2026, 12, day), hour, 0);
        var ai = new FakeChatClient(Summary);

        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
        Assert.Empty(ai.Calls);
    }

    [Fact]
    public async Task A_busy_user_gets_it_later_in_the_week()
    {
        _store.States[UserId] = new ConversationState(Stage.DetailGoals, "Rafael");
        var ai = new FakeChatClient(Summary);

        await Job(ai).RunAsync(CancellationToken.None);
        Assert.Empty(_channel.Sent);
        Assert.Empty(ai.Calls);
        Assert.Empty(_reporting.Summaries);

        _store.States[UserId] = new ConversationState(Stage.CollectGoals, "Rafael");
        _time.Now = At(new DateOnly(2026, 12, 2), 11, 0);
        await Job(ai).RunAsync(CancellationToken.None);
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task A_month_with_nothing_is_skipped_without_a_message()
    {
        _reporting.Report = Report(done: 0, missed: 0);
        var ai = new FakeChatClient(Summary);

        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Empty(_channel.Sent);
        Assert.Empty(ai.Calls);
        Assert.Equal(SummaryStatus.Skipped, Assert.Single(_reporting.Summaries).Status);
    }

    [Fact]
    public async Task Nothing_delivered_means_nothing_recorded()
    {
        _channel.Delivers = false;

        await Job(new FakeChatClient(Summary)).RunAsync(CancellationToken.None);

        Assert.Empty(_reporting.Summaries);
    }

    [Fact]
    public async Task The_insight_invitation_ends_the_summary_and_yes_reveals_it()
    {
        await Job(new FakeChatClient(WithInsight)).RunAsync(CancellationToken.None);

        var text = Assert.Single(_channel.Sent).Text;
        Assert.EndsWith("Quer saber qual?", text);
        Assert.DoesNotContain("3 vezes mais", text); // revealed only if accepted
        Assert.Equal(Stage.SummaryInsight, _store.States[UserId].Stage);

        var reply = await Workflow(new FakeChatClient("""{ "decision": "yes" }""")).ReplyAsync(Message("quero!"));

        Assert.Equal("Você lê 3 vezes mais nos fins de semana.", reply);
        Assert.Equal(InsightStatus.Revealed, _reporting.Summaries.Single().InsightStatus);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
        Assert.Null(_store.States[UserId].Draft!.SummaryId);
    }

    [Fact]
    public async Task No_thanks_moves_on_without_insisting()
    {
        await Job(new FakeChatClient(WithInsight)).RunAsync(CancellationToken.None);

        var reply = await Workflow(new FakeChatClient("""{ "decision": "no" }""")).ReplyAsync(Message("agora não"));

        Assert.Equal(SummaryInsightNode.Declined, reply);
        Assert.Equal(InsightStatus.Declined, _reporting.Summaries.Single().InsightStatus);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task Talking_about_something_else_drops_the_invitation_and_resumes_an_open_follow_up()
    {
        var goal = _goals.Saved.Single();
        var routine = (await _goals.ListRoutinesAsync(UserId)).Single();
        var checkIn = _checkIns.Add(UserId, goal.Id, routine.Id, new DateOnly(2026, 12, 1), "ler", _time.Now);
        _store.States[UserId] = new ConversationState(Stage.CheckIn, "Rafael", GoalsDraft.Empty(null) with
        {
            CheckIns = [new CheckInItem(checkIn.Id, goal.Id, goal.Name, "ler", checkIn.Day, null, null)],
        });

        await Job(new FakeChatClient(WithInsight)).RunAsync(CancellationToken.None);
        Assert.Equal(Stage.SummaryInsight, _store.States[UserId].Stage);

        var reply = await Workflow(new FakeChatClient(
            """{ "decision": "other" }""",
            """{ "decision": "report", "items": [ { "number": 1, "outcome": "done", "note": "leu" } ], "reply": "Boa leitura!" }"""))
            .ReplyAsync(Message("li hoje"));

        Assert.Equal("Boa leitura!", reply);
        Assert.Equal(InsightStatus.Offered, _reporting.Summaries.Single().InsightStatus); // never answered, never insisted
        Assert.Equal(CheckInStatus.Done, _checkIns.CheckIns.Single().Status);
        Assert.Equal(Stage.CollectGoals, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task After_the_insight_an_open_follow_up_is_asked_again()
    {
        var goal = _goals.Saved.Single();
        var checkIn = _checkIns.Add(UserId, goal.Id, Guid.CreateVersion7(), new DateOnly(2026, 12, 1), "ler", _time.Now);
        _store.States[UserId] = new ConversationState(Stage.CheckIn, "Rafael", GoalsDraft.Empty(null) with
        {
            CheckIns = [new CheckInItem(checkIn.Id, goal.Id, goal.Name, "ler", checkIn.Day, null, null)],
        });
        await Job(new FakeChatClient(WithInsight)).RunAsync(CancellationToken.None);

        var reply = await Workflow(new FakeChatClient("""{ "decision": "yes" }""")).ReplyAsync(Message("sim"));

        Assert.StartsWith("Você lê 3 vezes mais", reply);
        Assert.Contains("conseguiu ler hoje?", reply);
        Assert.Equal(Stage.CheckIn, _store.States[UserId].Stage);
    }

    [Fact]
    public async Task A_closed_quarter_asks_for_the_panorama_and_is_recorded_as_such()
    {
        _time.Now = At(new DateOnly(2027, 1, 1), 10, 0);
        var december = Report(done: 12, missed: 3, month: new DateOnly(2026, 12, 1));
        _reporting.Report = december with { Quarter = december.Month with { From = new DateOnly(2026, 10, 1) }, QuarterMonths = [december.Month] };
        var ai = new FakeChatClient(Summary);

        await Job(ai).RunAsync(CancellationToken.None);

        Assert.Contains("panorama curto do trimestre", ai.Calls[0][0].Text);
        Assert.True(Assert.Single(_reporting.Summaries).IncludesQuarter);
        Assert.Equal(new DateOnly(2026, 12, 1), _reporting.Summaries.Single().Month);
    }

    [Fact]
    public async Task An_unreadable_ai_answer_still_sends_a_plain_summary()
    {
        await Job(new FakeChatClient("not json")).RunAsync(CancellationToken.None);

        Assert.Contains("você fez 80% do que foi proposto", Assert.Single(_channel.Sent).Text);
    }

    [Fact]
    public async Task Without_enough_history_there_is_no_insight()
    {
        _reporting.Report = Report(done: 3, missed: 0);

        var written = await new SummaryWriter(new FakeChatClient(WithInsight)).WriteAsync(_reporting.Report, null, CancellationToken.None);

        Assert.Null(written.InsightTeaser);
        Assert.Null(written.Insight);
        Assert.Equal(written.Message, written.Text);
    }

    private MonthlyReportDto Report(int done, int missed, DateOnly? month = null)
    {
        var from = month ?? November;
        var goal = _goals.Saved.Single();
        var percent = done + missed == 0 ? (int?)null : (int)Math.Round(done * 100m / (done + missed), MidpointRounding.AwayFromZero);
        var stats = new GoalStatsDto(goal.Id, goal.Name, done, missed, 0, percent, [], null,
            Enumerable.Range(1, 7).Select(d => new WeekdayStatsDto(d, 0, 0)).ToList(), 12, "livros", null);
        var period = new PeriodStatsDto(from, from.AddMonths(1).AddDays(-1), done, missed, 0, percent, [stats]);
        var empty = new PeriodStatsDto(from.AddMonths(-1), from.AddDays(-1), 0, 0, 0, null, []);
        return new MonthlyReportDto(period, empty, null, []);
    }

    private MonthlySummaryJob Job(IChatClient ai) =>
        new(_goals, _reporting, _store, new SummaryWriter(ai), _journal, Workflow(ai), _time, NullLogger<MonthlySummaryJob>.Instance);

    private readonly FakeFitnessJournal _journal = new();

    private PippinWorkflow Workflow(IChatClient ai) =>
        new(
            _store,
            [
                new GreetingNode(), new CollectGoalsNode(ai, _time), new CheckInNode(ai, _progress, _checkIns, _time),
                new SummaryInsightNode(ai, _reporting),
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
