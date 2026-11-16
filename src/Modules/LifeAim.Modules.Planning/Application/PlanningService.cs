using LifeAim.Modules.Planning.Contracts;
using LifeAim.Modules.Planning.Infrastructure;

namespace LifeAim.Modules.Planning.Application;

internal sealed class PlanningService(ProposalRepository repository, TimeProvider timeProvider) : IPlanningService
{
    public Task<bool> RequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.RequestAsync(userId, timeProvider.GetUtcNow(), cancellationToken);

    public Task<IReadOnlyList<PlanProposalDto>> ListByStatusAsync(
        ProposalStatus status, int limit = 20, CancellationToken cancellationToken = default) =>
        repository.ListByStatusAsync(status, Math.Clamp(limit, 1, 100), cancellationToken);

    public Task<PlanProposalDto?> GetOpenAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.GetOpenAsync(userId, cancellationToken);

    public Task<IReadOnlyList<Guid>> AcceptedGoalIdsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.AcceptedGoalIdsAsync(userId, cancellationToken);

    public Task MarkReadyAsync(
        Guid proposalId, IReadOnlyList<GoalProposalInput> goals, CancellationToken cancellationToken = default)
    {
        // The texts are checked here so a bad AI answer fails in code, not as a database constraint.
        var valid = goals
            .Where(g => !string.IsNullOrWhiteSpace(g.Message) && !string.IsNullOrWhiteSpace(g.Question))
            .Select(g => g with
            {
                Message = Limit(g.Message.Trim(), 2000),
                Question = Limit(g.Question.Trim(), 500),
            })
            .DistinctBy(g => g.GoalId)
            .ToList();
        return repository.MarkReadyAsync(proposalId, valid, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task MarkFailedAttemptAsync(Guid proposalId, string error, CancellationToken cancellationToken = default) =>
        repository.MarkFailedAttemptAsync(proposalId, error, IPlanningService.MaxAttempts, cancellationToken);

    public Task MarkOfferedAsync(Guid proposalId, CancellationToken cancellationToken = default) =>
        repository.MarkOfferedAsync(proposalId, timeProvider.GetUtcNow(), cancellationToken);

    public Task SetGoalStatusAsync(
        Guid proposalId, Guid goalId, GoalProposalStatus status, CancellationToken cancellationToken = default) =>
        repository.SetGoalStatusAsync(proposalId, goalId, status, timeProvider.GetUtcNow(), cancellationToken);

    public Task CloseAsync(Guid proposalId, CancellationToken cancellationToken = default) =>
        repository.CloseAsync(proposalId, timeProvider.GetUtcNow(), cancellationToken);

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];
}
