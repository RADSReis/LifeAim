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
            timeProvider.GetUtcNow(),
            request.StartValue,
            request.TargetValue,
            request.Unit);

        await repository.AddAsync(goal, cancellationToken);
        return ToDto(goal);
    }

    public async Task<IReadOnlyList<GoalDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var goals = await repository.ListByUserAsync(userId, cancellationToken);
        return goals.Select(ToDto).ToList();
    }

    public async Task<GoalDto?> UpdateAsync(UpdateGoalRequest request, CancellationToken cancellationToken = default)
    {
        var goal = await repository.FindAsync(request.GoalId, request.UserId, cancellationToken);
        if (goal is null)
        {
            return null;
        }

        var changed = goal.With(
            request.Name, request.Description, request.Indicator, request.DueDate,
            request.StartValue, request.TargetValue, request.Unit);

        return await repository.UpdateAsync(changed, cancellationToken) ? ToDto(changed) : null;
    }

    private static GoalDto ToDto(Goal goal) =>
        new(
            goal.Id, goal.UserId, goal.Name, goal.Description, goal.Indicator, goal.DueDate,
            goal.StartValue, goal.TargetValue, goal.Unit, goal.CreatedAt);
}
