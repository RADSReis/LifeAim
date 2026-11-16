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
            request.Unit,
            request.UserPlan);

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
            request.StartValue, request.TargetValue, request.Unit, request.UserPlan);

        return await repository.UpdateAsync(changed, cancellationToken) ? ToDto(changed) : null;
    }

    public Task<bool> CancelAsync(Guid goalId, Guid userId, CancellationToken cancellationToken = default) =>
        CancelAsync(goalId, userId, null, cancellationToken);

    public Task<bool> CancelAsync(Guid goalId, Guid userId, string? reason, CancellationToken cancellationToken = default) =>
        repository.CancelAsync(goalId, userId, GoalLifecycle.ValidReason(reason), Now(), cancellationToken);

    public Task<bool> PauseAsync(
        Guid goalId, Guid userId, DateOnly? resumeOn, string? reason, CancellationToken cancellationToken = default)
    {
        var now = Now();
        var day = GoalLifecycle.ValidResumeOn(resumeOn, DateOnly.FromDateTime(now.UtcDateTime));
        return repository.PauseAsync(goalId, userId, day, GoalLifecycle.ValidReason(reason), now, cancellationToken);
    }

    public Task<bool> ResumeAsync(Guid goalId, Guid userId, CancellationToken cancellationToken = default) =>
        repository.ResumeAsync(goalId, userId, Now(), cancellationToken);

    public async Task<GoalDto?> ReplaceAsync(
        Guid goalId, CreateGoalRequest replacement, string? reason, CancellationToken cancellationToken = default)
    {
        var now = Now();
        var goal = Goal.Create(
            replacement.UserId, replacement.Name, replacement.Description, replacement.Indicator, replacement.DueDate, now,
            replacement.StartValue, replacement.TargetValue, replacement.Unit, replacement.UserPlan);

        return await repository.ReplaceAsync(goalId, replacement.UserId, goal, GoalLifecycle.ValidReason(reason), now, cancellationToken)
            ? ToDto(goal)
            : null;
    }

    public Task<IReadOnlyList<GoalDto>> ListHistoryAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.ListHistoryAsync(userId, cancellationToken);

    public Task<IReadOnlyList<GoalDto>> ListPausesEndingAsync(DateOnly today, CancellationToken cancellationToken = default) =>
        repository.ListPausesEndingAsync(today, cancellationToken);

    public Task<bool> MarkResumeOfferedAsync(Guid goalId, Guid userId, CancellationToken cancellationToken = default) =>
        repository.MarkResumeOfferedAsync(goalId, userId, Now(), cancellationToken);

    private DateTimeOffset Now() => Goal.ToDatabasePrecision(timeProvider.GetUtcNow());

    public async Task<IReadOnlyList<RoutineDto>?> ReplaceRoutinesAsync(
        Guid goalId,
        Guid userId,
        IReadOnlyList<RoutineInput> routines,
        CancellationToken cancellationToken = default)
    {
        if (await repository.FindAsync(goalId, userId, cancellationToken) is null)
        {
            return null;
        }

        // All validated before anything is written: with one invalid routine the plan stays as it was.
        // One microsecond apart, so they read back in the order they were given.
        var now = timeProvider.GetUtcNow();
        var created = routines
            .Select((input, i) => GoalRoutine.Create(goalId, userId, input, now.AddTicks(i * 10)))
            .ToList();

        await repository.ReplaceRoutinesAsync(goalId, userId, created, cancellationToken);
        return created.Select(ToDto).ToList();
    }

    public Task<IReadOnlyList<Guid>> ListUserIdsWithRoutinesAsync(CancellationToken cancellationToken = default) =>
        repository.ListUserIdsWithRoutinesAsync(cancellationToken);

    public async Task<IReadOnlyList<RoutineDto>> ListRoutinesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var routines = await repository.ListRoutinesByUserAsync(userId, cancellationToken);
        return routines.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<RoutineDto>> ListGoalRoutinesAsync(
        Guid goalId, Guid userId, CancellationToken cancellationToken = default)
    {
        var routines = await repository.ListRoutinesByUserAsync(userId, cancellationToken, goalId);
        return routines.Select(ToDto).ToList();
    }

    private static GoalDto ToDto(Goal goal) =>
        new(
            goal.Id, goal.UserId, goal.Name, goal.Description, goal.Indicator, goal.DueDate,
            goal.StartValue, goal.TargetValue, goal.Unit, goal.CreatedAt, goal.UserPlan);

    private static RoutineDto ToDto(GoalRoutine routine) =>
        new(
            routine.Id, routine.GoalId, routine.Activity, routine.Frequency, routine.Interval, routine.DaysOfWeek,
            routine.TimesPerPeriod, routine.TimeOfDay, routine.DayPeriod, routine.Amount, routine.Unit,
            routine.DurationMinutes, routine.Source, routine.StartsOn, routine.EndsOn, routine.CreatedAt);
}
