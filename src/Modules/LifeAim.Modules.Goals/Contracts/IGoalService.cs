namespace LifeAim.Modules.Goals.Contracts;

/// <summary>
/// The Goals module's public API. Other modules (Pippin's tools, in Agents) use only this,
/// never the module's tables or internal classes.
/// </summary>
public interface IGoalService
{
    Task<GoalDto> CreateAsync(CreateGoalRequest request, CancellationToken cancellationToken = default);

    /// <summary>The user's goals, oldest first.</summary>
    Task<IReadOnlyList<GoalDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the editable fields of one of the user's goals (everything but id, user and creation time).
    /// Null when the goal does not exist or belongs to someone else.
    /// </summary>
    Task<GoalDto?> UpdateAsync(UpdateGoalRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Indicator">What is measured, in words ("peso corporal").</param>
/// <param name="StartValue">Where the user is now (94).</param>
/// <param name="TargetValue">Where the user wants to get (90).</param>
/// <param name="Unit">The unit of both values ("kg").</param>
public sealed record CreateGoalRequest(
    Guid UserId,
    string Name,
    string? Description = null,
    string? Indicator = null,
    DateOnly? DueDate = null,
    decimal? StartValue = null,
    decimal? TargetValue = null,
    string? Unit = null);

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
    string? Unit);

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
    DateTimeOffset CreatedAt);
