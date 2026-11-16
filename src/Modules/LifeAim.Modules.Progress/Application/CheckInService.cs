using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Progress.Infrastructure;

namespace LifeAim.Modules.Progress.Application;

internal sealed class CheckInService(CheckInRepository repository, TimeProvider timeProvider) : ICheckInService
{
    public const int MaxActivityLength = 100;
    public const int MaxReasonLength = 500;

    public Task<IReadOnlyList<CheckInDto>> ListAsync(
        Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        repository.ListAsync(userId, from, to, cancellationToken);

    public Task<CheckInDto> MarkAskedAsync(AskCheckInRequest request, CancellationToken cancellationToken = default)
    {
        if (request.UserId == Guid.Empty || request.GoalId == Guid.Empty || request.RoutineId == Guid.Empty)
        {
            throw new ArgumentException("A check-in needs its user, goal and routine.", nameof(request));
        }

        var activity = Blank(request.Activity) ?? throw new ArgumentException("A check-in says what was planned.", nameof(request));
        if (activity.Length > MaxActivityLength)
        {
            activity = activity[..MaxActivityLength];
        }

        var now = ToDatabasePrecision(timeProvider.GetUtcNow());
        return repository.UpsertAskedAsync(Guid.CreateVersion7(now), request, activity, now, cancellationToken);
    }

    public Task<bool> MarkDoneAsync(
        Guid checkInId, Guid userId, Guid? progressEntryId, CancellationToken cancellationToken = default) =>
        repository.AnswerAsync(
            checkInId, userId, CheckInStatus.Done, null, progressEntryId, ToDatabasePrecision(timeProvider.GetUtcNow()),
            cancellationToken);

    public Task<bool> MarkMissedAsync(
        Guid checkInId, Guid userId, string? reason, CancellationToken cancellationToken = default)
    {
        var trimmed = Blank(reason);
        if (trimmed?.Length > MaxReasonLength)
        {
            trimmed = trimmed[..MaxReasonLength];
        }

        return repository.AnswerAsync(
            checkInId, userId, CheckInStatus.Missed, trimmed, null, ToDatabasePrecision(timeProvider.GetUtcNow()),
            cancellationToken);
    }

    public Task<int> MarkDoneForGoalAsync(
        Guid userId, Guid goalId, DateOnly day, Guid? progressEntryId, CancellationToken cancellationToken = default) =>
        repository.AnswerDoneForGoalAsync(
            userId, goalId, day, progressEntryId, ToDatabasePrecision(timeProvider.GetUtcNow()), cancellationToken);

    // timestamptz keeps microseconds; .NET keeps 100 ns ticks (the same rule as the progress entries).
    private static DateTimeOffset ToDatabasePrecision(DateTimeOffset moment)
    {
        var utc = moment.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
