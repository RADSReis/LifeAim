using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Goals.Domain;
using LifeAim.Modules.Goals.Infrastructure;

namespace LifeAim.Modules.Goals.Application;

internal sealed class GoalService(GoalRepository repository, TimeProvider timeProvider) : IGoalService
{
    public async Task<GoalDto> CreateAsync(CreateGoalRequest request, CancellationToken cancellationToken = default)
    {
        var goal = Goal.Create(
            request.UserId,
            request.Name,
            request.Description,
            request.Indicator,
            request.DueDate,
            timeProvider.GetUtcNow());

        await repository.AddAsync(goal, cancellationToken);
        return ToDto(goal);
    }

    public async Task<IReadOnlyList<GoalDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var goals = await repository.ListByUserAsync(userId, cancellationToken);
        return goals.Select(ToDto).ToList();
    }

    private static GoalDto ToDto(Goal goal) =>
        new(goal.Id, goal.UserId, goal.Name, goal.Description, goal.Indicator, goal.DueDate, goal.CreatedAt);
}
