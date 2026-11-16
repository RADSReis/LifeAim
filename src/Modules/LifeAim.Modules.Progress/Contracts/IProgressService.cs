namespace LifeAim.Modules.Progress.Contracts;

/// <summary>
/// The Progress module's public API: what the user actually did ("hoje li 10 páginas"), one entry per report.
/// Other modules (Pippin's tools, in Agents) use only this. The module does not know the Goals module: the
/// caller checks that the goal is the user's before recording.
/// </summary>
public interface IProgressService
{
    Task<ProgressEntryDto> RecordAsync(RecordProgressRequest request, CancellationToken cancellationToken = default);

    /// <summary>The goal's entries, newest first (at most <paramref name="limit"/>).</summary>
    Task<IReadOnlyList<ProgressEntryDto>> ListByGoalAsync(
        Guid userId,
        Guid goalId,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>All the user's entries from <paramref name="from"/> to <paramref name="to"/> (inclusive), oldest day first.</summary>
    Task<IReadOnlyList<ProgressEntryDto>> ListAsync(
        Guid userId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One report of progress. The amount does not have to be the goal's main indicator (Funcionalidade 1:
/// "10 páginas" in a goal measured in books): it is the effort, kept for the follow-up and the analyses.
/// </summary>
/// <param name="OccurredOn">The day it happened ("ontem" → yesterday), in the user's calendar.</param>
/// <param name="Amount">How much (10), when the user said a number.</param>
/// <param name="Unit">The unit of <paramref name="Amount"/> ("páginas", "kg", "km").</param>
/// <param name="Note">What was done, in short words ("leu 10 páginas"); always present.</param>
public sealed record RecordProgressRequest(
    Guid UserId,
    Guid GoalId,
    DateOnly OccurredOn,
    string Note,
    decimal? Amount = null,
    string? Unit = null);

public sealed record ProgressEntryDto(
    Guid Id,
    Guid UserId,
    Guid GoalId,
    DateOnly OccurredOn,
    decimal? Amount,
    string? Unit,
    string Note,
    DateTimeOffset CreatedAt);
