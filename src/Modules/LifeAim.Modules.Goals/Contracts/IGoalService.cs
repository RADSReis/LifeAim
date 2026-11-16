namespace LifeAim.Modules.Goals.Contracts;

/// <summary>
/// The Goals module's public API. Other modules (Pippin's tools, in Agents) use only this,
/// never the module's tables or internal classes.
/// </summary>
public interface IGoalService
{
    Task<GoalDto> CreateAsync(CreateGoalRequest request, CancellationToken cancellationToken = default);

    /// <summary>The user's active goals, oldest first (cancelled ones are kept, but not listed).</summary>
    Task<IReadOnlyList<GoalDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels one of the user's active goals: it leaves every list, but its row, plan and history are kept
    /// (Funcionalidade 6). False when there is no such active goal.
    /// </summary>
    Task<bool> CancelAsync(Guid goalId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the editable fields of one of the user's goals (everything but id, user and creation time).
    /// Null when the goal does not exist or belongs to someone else.
    /// </summary>
    Task<GoalDto?> UpdateAsync(UpdateGoalRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the whole plan of one of the user's goals with these routines (an empty list clears it).
    /// Null when the goal does not exist or belongs to someone else.
    /// </summary>
    Task<IReadOnlyList<RoutineDto>?> ReplaceRoutinesAsync(
        Guid goalId,
        Guid userId,
        IReadOnlyList<RoutineInput> routines,
        CancellationToken cancellationToken = default);

    /// <summary>The routines of all the user's active goals, in the order they were created.</summary>
    Task<IReadOnlyList<RoutineDto>> ListRoutinesAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <param name="Indicator">What is measured, in words ("peso corporal").</param>
/// <param name="StartValue">Where the user is now (94).</param>
/// <param name="TargetValue">Where the user wants to get (90).</param>
/// <param name="Unit">The unit of both values ("kg").</param>
/// <param name="UserPlan">How the user intends to reach it, exactly as written.</param>
public sealed record CreateGoalRequest(
    Guid UserId,
    string Name,
    string? Description = null,
    string? Indicator = null,
    DateOnly? DueDate = null,
    decimal? StartValue = null,
    decimal? TargetValue = null,
    string? Unit = null,
    string? UserPlan = null);

/// <summary>The new values of every editable field; a null clears that field.</summary>
public sealed record UpdateGoalRequest(
    Guid GoalId,
    Guid UserId,
    string Name,
    string? Description,
    string? Indicator,
    DateOnly? DueDate,
    decimal? StartValue,
    decimal? TargetValue,
    string? Unit,
    string? UserPlan);

public sealed record GoalDto(
    Guid Id,
    Guid UserId,
    string Name,
    string? Description,
    string? Indicator,
    DateOnly? DueDate,
    decimal? StartValue,
    decimal? TargetValue,
    string? Unit,
    DateTimeOffset CreatedAt,
    string? UserPlan = null);

public enum RoutineFrequency
{
    Daily,
    Weekly,
    Monthly,
}

/// <summary>An approximate part of the day ("de manhã"), for when the user did not say an exact time.</summary>
public enum DayPeriod
{
    Morning,
    Afternoon,
    Evening,
    Night,
}

/// <summary>Who came up with the routine: the user described it, or Pippin proposed it.</summary>
public enum RoutineSource
{
    User,
    Pippin,
}

/// <summary>
/// One recurring activity of a goal's plan, e.g. "ler seg, qua e sex de manhã, 10 páginas":
/// Activity "ler", Weekly, DaysOfWeek [1, 3, 5], DayPeriod Morning, Amount 10, Unit "páginas".
/// </summary>
/// <param name="Interval">Every how many periods (2 with Weekly = every other week).</param>
/// <param name="DaysOfWeek">ISO weekdays, 1 = Monday … 7 = Sunday; only for Weekly; empty when not fixed.</param>
/// <param name="TimesPerPeriod">"3 vezes por semana" without fixed days (then DaysOfWeek is empty).</param>
/// <param name="TimeOfDay">Only when the user said an exact time.</param>
public sealed record RoutineInput(
    string Activity,
    RoutineFrequency Frequency,
    RoutineSource Source,
    int Interval = 1,
    IReadOnlyList<int>? DaysOfWeek = null,
    int? TimesPerPeriod = null,
    TimeOnly? TimeOfDay = null,
    DayPeriod? DayPeriod = null,
    decimal? Amount = null,
    string? Unit = null,
    int? DurationMinutes = null,
    DateOnly? StartsOn = null,
    DateOnly? EndsOn = null);

public sealed record RoutineDto(
    Guid Id,
    Guid GoalId,
    string Activity,
    RoutineFrequency Frequency,
    int Interval,
    IReadOnlyList<int> DaysOfWeek,
    int? TimesPerPeriod,
    TimeOnly? TimeOfDay,
    DayPeriod? DayPeriod,
    decimal? Amount,
    string? Unit,
    int? DurationMinutes,
    RoutineSource Source,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    DateTimeOffset CreatedAt);
