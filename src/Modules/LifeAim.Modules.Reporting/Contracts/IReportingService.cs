namespace LifeAim.Modules.Reporting.Contracts;

/// <summary>
/// The Reporting module's public API (Funcionalidade 4, "Resumos mensais e panorama trimestral"): the numbers of a
/// month (and of the quarter it closes), counted from the plan, the progress and the follow-up, and the record of the
/// summaries already sent, so each month is summarized once. Who writes and sends the message (Pippin) lives in Agents.
/// </summary>
public interface IReportingService
{
    /// <summary>
    /// The numbers of <paramref name="month"/> (any day of it), of the month before (to compare) and, when it closes a
    /// quarter (March, June, September, December), of the quarter and each of its months.
    /// </summary>
    Task<MonthlyReportDto> BuildMonthlyAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default);

    /// <summary>The summary of that month, if it was already sent or skipped.</summary>
    Task<SummaryDto?> GetForMonthAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default);

    /// <summary>One of the user's summaries; null when it does not exist or is someone else's.</summary>
    Task<SummaryDto?> FindAsync(Guid summaryId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Pippin sent the summary (with an insight invitation or not). False when that month already had one.</summary>
    Task<bool> RecordSentAsync(RecordSummaryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Nothing happened that month (no plan, no progress): no message, and the month is not looked at again.</summary>
    Task<bool> RecordSkippedAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default);

    /// <summary>The user answered the insight invitation. False when it was not waiting for an answer.</summary>
    Task<bool> AnswerInsightAsync(Guid summaryId, Guid userId, bool accepted, CancellationToken cancellationToken = default);
}

/// <summary>What was done in a period. Only the days whose result is known count for the percentage.</summary>
/// <param name="Done">Days something was done (a progress entry, or the follow-up answered "sim").</param>
/// <param name="Missed">Days the user said it did not happen.</param>
/// <param name="Unconfirmed">Days Pippin asked and nobody answered: not counted as done nor as missed.</param>
/// <param name="DonePercent">Done ÷ (Done + Missed), rounded; null when no result is known.</param>
public sealed record PeriodStatsDto(
    DateOnly From,
    DateOnly To,
    int Done,
    int Missed,
    int Unconfirmed,
    int? DonePercent,
    IReadOnlyList<GoalStatsDto> Goals);

/// <param name="Totals">The amounts reported in the period, added up per unit (120 "páginas").</param>
/// <param name="ByWeekday">ISO weekday (1 = Monday) → days done and the amount in <paramref name="MainUnit"/>, for patterns.</param>
/// <param name="MainUnit">The unit the user reports most for this goal, if any.</param>
public sealed record GoalStatsDto(
    Guid GoalId,
    string Name,
    int Done,
    int Missed,
    int Unconfirmed,
    int? DonePercent,
    IReadOnlyList<AmountTotalDto> Totals,
    string? MainUnit,
    IReadOnlyList<WeekdayStatsDto> ByWeekday,
    decimal? TargetValue,
    string? Unit,
    DateOnly? DueDate);

public sealed record AmountTotalDto(string Unit, decimal Total);

public sealed record WeekdayStatsDto(int IsoDay, int DoneDays, decimal Amount);

/// <param name="Month">The month summarized.</param>
/// <param name="Quarter">The quarter the month closes; null for the other months.</param>
/// <param name="QuarterMonths">With <paramref name="Quarter"/>: each of its three months, oldest first.</param>
public sealed record MonthlyReportDto(
    PeriodStatsDto Month,
    PeriodStatsDto Previous,
    PeriodStatsDto? Quarter,
    IReadOnlyList<PeriodStatsDto> QuarterMonths)
{
    /// <summary>Nothing to summarize: no goal had a plan or any activity in the month.</summary>
    public bool IsEmpty => Month.Goals.Count == 0 || Month.Goals.All(g => g.Done + g.Missed + g.Unconfirmed == 0);
}

/// <param name="InsightTeaser">The invitation ("Tive um insight sobre seu hábito de leitura. Quer saber qual?").</param>
/// <param name="Insight">What is revealed if the user accepts.</param>
public sealed record RecordSummaryRequest(
    Guid SummaryId,
    Guid UserId,
    DateOnly Month,
    bool IncludesQuarter,
    string Message,
    string? InsightTeaser,
    string? Insight);

public enum SummaryStatus
{
    Sent,
    Skipped,
}

public enum InsightStatus
{
    None,
    Offered,
    Revealed,
    Declined,
}

public sealed record SummaryDto(
    Guid Id,
    Guid UserId,
    DateOnly Month,
    SummaryStatus Status,
    bool IncludesQuarter,
    string? Message,
    string? InsightTeaser,
    string? Insight,
    InsightStatus InsightStatus,
    DateTimeOffset CreatedAt);
