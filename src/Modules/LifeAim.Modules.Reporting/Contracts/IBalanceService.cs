namespace LifeAim.Modules.Reporting.Contracts;

/// <summary>
/// Funcionalidade 5, "Gerenciamento de prioridades e conflitos entre metas": reads the recent history of every goal
/// and finds, in plain code, the signals that justify a conversation (a goal with recurring difficulty, two goals
/// dropping at the same time, misses because of lack of time or energy). It also says which goals are working well, so
/// they are protected, and what was done in absolute terms (with a projection, always an estimate). Keeps the record
/// of the reviews Pippin started, so the user is not asked again too soon. Who writes and sends them lives in Agents.
/// </summary>
public interface IBalanceService
{
    /// <summary>The last <see cref="BalanceRules.WindowDays"/> days up to <paramref name="today"/> (the user's calendar).</summary>
    Task<BalanceDto> AnalyzeAsync(Guid userId, DateOnly today, CancellationToken cancellationToken = default);

    /// <summary>The user's most recent review, if any.</summary>
    Task<BalanceReviewDto?> GetLatestAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>One of the user's reviews; null when it does not exist or is someone else's.</summary>
    Task<BalanceReviewDto?> FindAsync(Guid reviewId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Pippin sent the review message about <see cref="RecordReviewRequest.FocusGoalId"/>.</summary>
    Task<bool> RecordSentAsync(RecordReviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user answered: what is in the way (<see cref="ReviewStatus.Explained"/>), then whether the adjustment was
    /// accepted or declined. False when the review is already closed or is someone else's.
    /// </summary>
    Task<bool> AnswerAsync(
        Guid reviewId,
        Guid userId,
        ReviewStatus status,
        string? obstacle,
        CancellationToken cancellationToken = default);
}

/// <summary>The thresholds of the rules, in one place (and in the Notion page).</summary>
public static class BalanceRules
{
    /// <summary>How far back the analysis looks: two halves of 14 days, to compare.</summary>
    public const int WindowDays = 28;

    /// <summary>A goal "working well": at least this many days with a known result…</summary>
    public const int MinKnownDays = 4;

    /// <summary>…and at least this percentage done. It is protected: never reduced to make room for another.</summary>
    public const int ThrivingPercent = 70;

    /// <summary>Recurring difficulty: at least this many "não fiz" in the window (one bad day is not a pattern)…</summary>
    public const int RecurringMisses = 3;

    /// <summary>…and less than this percentage done.</summary>
    public const int StrugglingPercent = 50;

    /// <summary>A drop: the last 14 days at least this many points below the 14 before…</summary>
    public const int DropPoints = 25;

    /// <summary>…with at least this many known days in each half; and it takes two goals or more at once.</summary>
    public const int MinKnownDaysPerHalf = 3;

    /// <summary>Misses whose reason is lack of time or energy ("não deu tempo", "cansado") in the window.</summary>
    public const int TimeOrEnergyMisses = 3;

    /// <summary>After a review, no other for this many days.</summary>
    public const int CooldownDays = 14;
}

public enum BalanceSignal
{
    /// <summary>One goal keeps not happening.</summary>
    RecurringDifficulty,

    /// <summary>Two goals or more dropped in the same weeks: they may be competing for the same time.</summary>
    SimultaneousDrop,

    /// <summary>The user keeps saying there was no time or no energy.</summary>
    LackOfTimeOrEnergy,
}

public enum GoalHealth
{
    /// <summary>Too few known results to say anything.</summary>
    NotEnoughData,

    /// <summary>Working: keep it as it is.</summary>
    Thriving,

    Steady,

    /// <summary>Recurring difficulty.</summary>
    Struggling,
}

/// <param name="FocusGoalId">
/// The one goal worth a conversation now (never a thriving one); null when no signal justifies an intervention.
/// </param>
public sealed record BalanceDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<GoalBalanceDto> Goals,
    IReadOnlyList<BalanceSignalDto> Signals,
    Guid? FocusGoalId)
{
    public bool NeedsReview => FocusGoalId is not null;

    public GoalBalanceDto? Focus => Goals.FirstOrDefault(g => g.GoalId == FocusGoalId);

    public IEnumerable<GoalBalanceDto> Thriving => Goals.Where(g => g.Health == GoalHealth.Thriving);
}

/// <param name="Done">Days done in the window (unconfirmed days are neither done nor missed).</param>
/// <param name="RecentPercent">The last 14 days; null without known results.</param>
/// <param name="EarlierPercent">The 14 days before; null without known results.</param>
/// <param name="MissReasons">What the user said when it did not happen, newest first.</param>
/// <param name="TotalSoFar">Everything reported since the goal was created, in <paramref name="MainUnit"/>.</param>
public sealed record GoalBalanceDto(
    Guid GoalId,
    string Name,
    int Done,
    int Missed,
    int Unconfirmed,
    int? DonePercent,
    int? RecentPercent,
    int? EarlierPercent,
    GoalHealth Health,
    IReadOnlyList<string> MissReasons,
    string? MainUnit,
    decimal? TotalSoFar,
    ProjectionDto? Projection);

/// <summary>
/// Where the goal gets if the current pace holds: an estimate, never a promise. Only for goals that add up (no start
/// value, like "ler 12 livros") whose progress is reported in the unit of the target.
/// </summary>
public sealed record ProjectionDto(decimal SoFar, decimal Projected, decimal Target, string Unit, DateOnly DueDate);

public sealed record BalanceSignalDto(BalanceSignal Kind, IReadOnlyList<Guid> GoalIds);

public enum ReviewStatus
{
    /// <summary>Pippin sent the review and asked what is in the way.</summary>
    Offered,

    /// <summary>The user said what is in the way; Pippin may have proposed a small adjustment.</summary>
    Explained,

    /// <summary>The adjustment was saved.</summary>
    Accepted,

    /// <summary>The user preferred to keep the plan as it is.</summary>
    Declined,
}

/// <param name="Signals">Why the review was started.</param>
/// <param name="ProtectedGoalIds">The goals working well, which the adjustment must not touch.</param>
public sealed record RecordReviewRequest(
    Guid ReviewId,
    Guid UserId,
    Guid FocusGoalId,
    IReadOnlyList<BalanceSignal> Signals,
    IReadOnlyList<Guid> ProtectedGoalIds,
    string Message);

public sealed record BalanceReviewDto(
    Guid Id,
    Guid UserId,
    Guid FocusGoalId,
    IReadOnlyList<BalanceSignal> Signals,
    IReadOnlyList<Guid> ProtectedGoalIds,
    string Message,
    ReviewStatus Status,
    string? Obstacle,
    DateTimeOffset CreatedAt);
