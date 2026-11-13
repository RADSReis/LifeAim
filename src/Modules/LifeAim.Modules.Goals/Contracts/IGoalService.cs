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
}

public sealed record CreateGoalRequest(
    Guid UserId,
    string Name,
    string? Description = null,
    string? Indicator = null,
    DateOnly? DueDate = null);

public sealed record GoalDto(
    Guid Id,
    Guid UserId,
    string Name,
    string? Description,
    string? Indicator,
    DateOnly? DueDate,
    DateTimeOffset CreatedAt);
