using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using LifeAim.Modules.Reporting.Infrastructure;

namespace LifeAim.Modules.Reporting.Application;

/// <summary>Reads the other modules only through their contracts, and keeps only its own table (reporting.balance_reviews).</summary>
internal sealed class BalanceService(
    IGoalService goals,
    IProgressService progress,
    ICheckInService checkIns,
    BalanceReviewRepository repository,
    TimeProvider timeProvider) : IBalanceService
{
    public const int MaxMessageLength = 4000;
    public const int MaxObstacleLength = 1000;

    public async Task<BalanceDto> AnalyzeAsync(Guid userId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var from = today.AddDays(-(BalanceRules.WindowDays - 1));
        var userGoals = await goals.ListAsync(userId, cancellationToken);
        var routines = await goals.ListRoutinesAsync(userId, cancellationToken);

        // Everything since the oldest goal, for the absolute progress ("já são 240 páginas") and the projection.
        var since = userGoals.Count == 0
            ? from
            : DateOnly.FromDateTime(userGoals.Min(g => g.CreatedAt).UtcDateTime.AddDays(-1));
        var entries = await progress.ListAsync(userId, since < from ? since : from, today, cancellationToken);
        var asked = await checkIns.ListAsync(userId, from, today, cancellationToken);

        return BalanceAnalyzer.Analyze(today, userGoals, routines, entries, asked);
    }

    public Task<BalanceReviewDto?> GetLatestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.GetLatestAsync(userId, cancellationToken);

    public Task<BalanceReviewDto?> FindAsync(Guid reviewId, Guid userId, CancellationToken cancellationToken = default) =>
        repository.FindAsync(reviewId, userId, cancellationToken);

    public Task<bool> RecordSentAsync(RecordReviewRequest request, CancellationToken cancellationToken = default)
    {
        if (request.UserId == Guid.Empty || request.ReviewId == Guid.Empty || request.FocusGoalId == Guid.Empty)
        {
            throw new ArgumentException("A review needs its id, its user and the goal it is about.", nameof(request));
        }

        if (request.Signals.Count == 0)
        {
            throw new ArgumentException("A review starts only from a signal.", nameof(request));
        }

        if (request.ProtectedGoalIds.Contains(request.FocusGoalId))
        {
            throw new ArgumentException("A goal that is working well is never the one to change.", nameof(request));
        }

        var message = Cut(Blank(request.Message), MaxMessageLength)
                      ?? throw new ArgumentException("A sent review has its message.", nameof(request));

        return repository.AddAsync(
            new BalanceReviewDto(
                request.ReviewId, request.UserId, request.FocusGoalId, request.Signals.Distinct().ToList(),
                request.ProtectedGoalIds.Distinct().ToList(), message, ReviewStatus.Offered, null, Now()),
            cancellationToken);
    }

    public Task<bool> AnswerAsync(
        Guid reviewId, Guid userId, ReviewStatus status, string? obstacle, CancellationToken cancellationToken = default)
    {
        if (status == ReviewStatus.Offered)
        {
            throw new ArgumentException("An answer moves the review on from offered.", nameof(status));
        }

        return repository.AnswerAsync(
            reviewId, userId, status, Cut(Blank(obstacle), MaxObstacleLength), Now(), cancellationToken);
    }

    // timestamptz keeps microseconds; .NET keeps 100 ns ticks (the same rule as the other modules).
    private DateTimeOffset Now()
    {
        var utc = timeProvider.GetUtcNow().ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Cut(string? value, int max) => value?.Length > max ? value[..max] : value;
}
