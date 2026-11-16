using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using LifeAim.Modules.Reporting.Infrastructure;

namespace LifeAim.Modules.Reporting.Application;

/// <summary>Reads the other modules only through their contracts, and keeps only its own table (reporting.summaries).</summary>
internal sealed class ReportingService(
    IGoalService goals,
    IProgressService progress,
    ICheckInService checkIns,
    SummaryRepository repository,
    TimeProvider timeProvider) : IReportingService
{
    public const int MaxMessageLength = 4000;
    public const int MaxTeaserLength = 500;
    public const int MaxInsightLength = 2000;

    public async Task<MonthlyReportDto> BuildMonthlyAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default)
    {
        var first = ReportCalculator.FirstDay(month);
        var last = ReportCalculator.LastDay(month);
        var closesQuarter = ReportCalculator.ClosesQuarter(month);
        var from = closesQuarter ? first.AddMonths(-2) : first.AddMonths(-1);

        var userGoals = await goals.ListAsync(userId, cancellationToken);
        var routines = await goals.ListRoutinesAsync(userId, cancellationToken);
        var entries = await progress.ListAsync(userId, from, last, cancellationToken);
        var asked = await checkIns.ListAsync(userId, from, last, cancellationToken);

        PeriodStatsDto Period(DateOnly start, DateOnly end) =>
            ReportCalculator.Period(start, end, userGoals, routines, entries, asked);

        var previous = first.AddMonths(-1);
        var quarterMonths = closesQuarter
            ? Enumerable.Range(0, 3)
                .Select(i => first.AddMonths(i - 2))
                .Select(m => Period(m, ReportCalculator.LastDay(m)))
                .ToList()
            : [];

        return new MonthlyReportDto(
            Period(first, last),
            Period(previous, ReportCalculator.LastDay(previous)),
            closesQuarter ? Period(first.AddMonths(-2), last) : null,
            quarterMonths);
    }

    public Task<SummaryDto?> GetForMonthAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default) =>
        repository.GetForMonthAsync(userId, ReportCalculator.FirstDay(month), cancellationToken);

    public Task<SummaryDto?> FindAsync(Guid summaryId, Guid userId, CancellationToken cancellationToken = default) =>
        repository.FindAsync(summaryId, userId, cancellationToken);

    public Task<bool> RecordSentAsync(RecordSummaryRequest request, CancellationToken cancellationToken = default)
    {
        if (request.UserId == Guid.Empty || request.SummaryId == Guid.Empty)
        {
            throw new ArgumentException("A summary needs its id and its user.", nameof(request));
        }

        var message = Cut(Blank(request.Message), MaxMessageLength)
                      ?? throw new ArgumentException("A sent summary has its message.", nameof(request));

        // An invitation only makes sense with something to reveal, and the other way round.
        var teaser = Cut(Blank(request.InsightTeaser), MaxTeaserLength);
        var insight = Cut(Blank(request.Insight), MaxInsightLength);
        if (teaser is null || insight is null)
        {
            (teaser, insight) = (null, null);
        }

        return repository.AddAsync(
            new SummaryDto(
                request.SummaryId, request.UserId, ReportCalculator.FirstDay(request.Month), SummaryStatus.Sent,
                request.IncludesQuarter, message, teaser, insight, insight is null ? InsightStatus.None : InsightStatus.Offered,
                Now()),
            cancellationToken);
    }

    public Task<bool> RecordSkippedAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default) =>
        repository.AddAsync(
            new SummaryDto(
                Guid.CreateVersion7(), userId, ReportCalculator.FirstDay(month), SummaryStatus.Skipped,
                ReportCalculator.ClosesQuarter(month), null, null, null, InsightStatus.None, Now()),
            cancellationToken);

    public Task<bool> AnswerInsightAsync(Guid summaryId, Guid userId, bool accepted, CancellationToken cancellationToken = default) =>
        repository.AnswerInsightAsync(
            summaryId, userId, accepted ? InsightStatus.Revealed : InsightStatus.Declined, Now(), cancellationToken);

    // timestamptz keeps microseconds; .NET keeps 100 ns ticks (the same rule as the other modules).
    private DateTimeOffset Now()
    {
        var utc = timeProvider.GetUtcNow().ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Cut(string? value, int max) => value?.Length > max ? value[..max] : value;
}
