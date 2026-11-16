using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>The monthly summaries (Funcionalidade 4) against a real, migrated PostgreSQL.</summary>
public sealed class ReportingServiceTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    private static readonly DateOnly November = new(2026, 11, 1);

    private readonly Guid _userId = Guid.CreateVersion7();

    private IReportingService Reporting => factory.Services.GetRequiredService<IReportingService>();
    private IGoalService Goals => factory.Services.GetRequiredService<IGoalService>();
    private IProgressService Progress => factory.Services.GetRequiredService<IProgressService>();
    private ICheckInService CheckIns => factory.Services.GetRequiredService<ICheckInService>();

    [Fact]
    public async Task The_month_is_counted_from_progress_and_follow_up_through_the_other_modules()
    {
        var goal = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ler 12 livros", TargetValue: 12, Unit: "livros"));
        var routines = await Goals.ReplaceRoutinesAsync(
            goal.Id, _userId, [new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User, Amount: 10, Unit: "páginas")]);
        var routineId = routines!.Single().Id;

        await Progress.RecordAsync(new RecordProgressRequest(_userId, goal.Id, new DateOnly(2026, 11, 2), "leu 10 páginas", 10, "páginas"));
        await Progress.RecordAsync(new RecordProgressRequest(_userId, goal.Id, new DateOnly(2026, 11, 3), "leu 20 páginas", 20, "páginas"));
        await Progress.RecordAsync(new RecordProgressRequest(_userId, goal.Id, new DateOnly(2026, 10, 30), "leu 5 páginas", 5, "páginas"));
        var missed = await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, goal.Id, routineId, new DateOnly(2026, 11, 4), "ler"));
        await CheckIns.MarkMissedAsync(missed.Id, _userId, "cansado");
        await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, goal.Id, routineId, new DateOnly(2026, 11, 5), "ler"));

        var report = await Reporting.BuildMonthlyAsync(_userId, new DateOnly(2026, 11, 20));

        Assert.Equal((November, new DateOnly(2026, 11, 30)), (report.Month.From, report.Month.To));
        var reading = Assert.Single(report.Month.Goals);
        Assert.Equal((2, 1, 1, 67), (reading.Done, reading.Missed, reading.Unconfirmed, reading.DonePercent));
        Assert.Equal(30m, Assert.Single(reading.Totals).Total);
        Assert.Equal(1, report.Previous.Done); // October, to compare
        Assert.Null(report.Quarter); // November closes no quarter
        Assert.False(report.IsEmpty);
    }

    [Fact]
    public async Task A_month_is_summarized_once_and_the_insight_is_answered_once()
    {
        var id = Guid.CreateVersion7();
        Assert.True(await Reporting.RecordSentAsync(new RecordSummaryRequest(
            id, _userId, new DateOnly(2026, 11, 30), false, "Você fez 80%!", "Tive um insight. Quer saber qual?", "Você lê mais aos sábados.")));
        Assert.False(await Reporting.RecordSentAsync(new RecordSummaryRequest(
            Guid.CreateVersion7(), _userId, November, false, "de novo", null, null)));
        Assert.False(await Reporting.RecordSkippedAsync(_userId, November));

        var summary = await Reporting.GetForMonthAsync(_userId, November);
        Assert.Equal((id, November, SummaryStatus.Sent, InsightStatus.Offered), (summary!.Id, summary.Month, summary.Status, summary.InsightStatus));

        Assert.True(await Reporting.AnswerInsightAsync(id, _userId, accepted: true));
        Assert.False(await Reporting.AnswerInsightAsync(id, _userId, accepted: false));
        Assert.Equal(InsightStatus.Revealed, (await Reporting.FindAsync(id, _userId))!.InsightStatus);
        Assert.Null(await Reporting.FindAsync(id, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task A_teaser_without_an_insight_is_not_an_invitation()
    {
        var id = Guid.CreateVersion7();
        await Reporting.RecordSentAsync(new RecordSummaryRequest(id, _userId, November, true, "Resumo", "Quer saber?", " "));

        var summary = await Reporting.FindAsync(id, _userId);

        Assert.Equal((InsightStatus.None, (string?)null, true), (summary!.InsightStatus, summary.InsightTeaser, summary.IncludesQuarter));
    }

    [Fact]
    public async Task A_skipped_month_is_kept_so_it_is_not_looked_at_again()
    {
        Assert.True(await Reporting.RecordSkippedAsync(_userId, November));

        var summary = await Reporting.GetForMonthAsync(_userId, November);

        Assert.Equal((SummaryStatus.Skipped, (string?)null), (summary!.Status, summary.Message));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        foreach (var table in new[]
                 {
                     "reporting.summaries", "progress.check_ins", "progress.progress_entries", "goals.goal_routines", "goals.goals",
                 })
        {
            await using var command = dataSource.CreateCommand($"DELETE FROM {table} WHERE user_id = $1");
            command.Parameters.Add(new() { Value = _userId });
            await command.ExecuteNonQueryAsync();
        }
    }
}
