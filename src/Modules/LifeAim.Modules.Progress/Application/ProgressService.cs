using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Progress.Domain;
using LifeAim.Modules.Progress.Infrastructure;

namespace LifeAim.Modules.Progress.Application;

internal sealed class ProgressService(ProgressRepository repository, TimeProvider timeProvider) : IProgressService
{
    public async Task<ProgressEntryDto> RecordAsync(RecordProgressRequest request, CancellationToken cancellationToken = default)
    {
        var entry = ProgressEntry.Create(
            request.UserId,
            request.GoalId,
            request.OccurredOn,
            request.Note,
            request.Amount,
            request.Unit,
            timeProvider.GetUtcNow());

        await repository.AddAsync(entry, cancellationToken);
        return ToDto(entry);
    }

    public async Task<IReadOnlyList<ProgressEntryDto>> ListByGoalAsync(
        Guid userId,
        Guid goalId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var entries = await repository.ListByGoalAsync(userId, goalId, Math.Clamp(limit, 1, 500), cancellationToken);
        return entries.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ProgressEntryDto>> ListAsync(
        Guid userId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var entries = await repository.ListByPeriodAsync(userId, from, to, cancellationToken);
        return entries.Select(ToDto).ToList();
    }

    private static ProgressEntryDto ToDto(ProgressEntry entry) =>
        new(entry.Id, entry.UserId, entry.GoalId, entry.OccurredOn, entry.Amount, entry.Unit, entry.Note, entry.CreatedAt);
}
