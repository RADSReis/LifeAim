using LifeAim.Modules.Goals.Contracts;

namespace LifeAim.Modules.Planning.Contracts;

/// <summary>
/// The Planning module's public API (Funcionalidade 2, "Planejamento inicial em segundo plano"): the first plan
/// proposal of a user's goals, prepared in the background, offered when ready and refined one goal at a time.
/// The module only keeps the proposals; who writes them (the AI) and who talks about them (Pippin) live in Agents.
/// </summary>
public interface IPlanningService
{
    /// <summary>
    /// Asks for a new proposal of the user's goals. A proposal already ready or being refined is replaced (closed),
    /// so the new one covers the goals added since. False when one is already waiting to be prepared.
    /// </summary>
    Task<bool> RequestAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The proposals at <paramref name="status"/>, oldest first (for the background job).</summary>
    Task<IReadOnlyList<PlanProposalDto>> ListByStatusAsync(
        ProposalStatus status,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>The user's proposal that is ready or being refined, with its goals; null when there is none.</summary>
    Task<PlanProposalDto?> GetOpenAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The goals whose plan the user already agreed on in some proposal: a new one leaves them out.</summary>
    Task<IReadOnlyList<Guid>> AcceptedGoalIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The proposal was written: it becomes ready to offer. With no goals at all it is just closed.</summary>
    Task MarkReadyAsync(Guid proposalId, IReadOnlyList<GoalProposalInput> goals, CancellationToken cancellationToken = default);

    /// <summary>Writing it failed: counted, and after <see cref="MaxAttempts"/> the proposal is given up (failed).</summary>
    Task MarkFailedAttemptAsync(Guid proposalId, string error, CancellationToken cancellationToken = default);

    /// <summary>Pippin sent the user the invitation to see it.</summary>
    Task MarkOfferedAsync(Guid proposalId, CancellationToken cancellationToken = default);

    /// <summary>The user agreed on (or skipped) the proposal of one goal.</summary>
    Task SetGoalStatusAsync(
        Guid proposalId,
        Guid goalId,
        GoalProposalStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>Every goal was talked about (or the proposal is no longer needed).</summary>
    Task CloseAsync(Guid proposalId, CancellationToken cancellationToken = default);

    const int MaxAttempts = 3;
}

/// <summary>requested → ready → offered → closed; or failed, after too many attempts to write it.</summary>
public enum ProposalStatus
{
    Requested,
    Ready,
    Offered,
    Closed,
    Failed,
}

public enum GoalProposalStatus
{
    Open,
    Accepted,
    Skipped,
}

/// <summary>The proposal for one goal, as written in the background.</summary>
/// <param name="Message">The concrete suggestion and its short reasoning ("10 páginas por dia terminam o livro em 20 dias").</param>
/// <param name="Question">The one contextual question that adapts the suggestion to the user's routine.</param>
/// <param name="Routines">The suggested plan, in the same shape as the goal's routines.</param>
public sealed record GoalProposalInput(Guid GoalId, string Message, string Question, IReadOnlyList<RoutineInput> Routines);

public sealed record GoalProposalDto(
    Guid GoalId,
    int Position,
    string Message,
    string Question,
    IReadOnlyList<RoutineInput> Routines,
    GoalProposalStatus Status);

public sealed record PlanProposalDto(
    Guid Id,
    Guid UserId,
    ProposalStatus Status,
    int Attempts,
    DateTimeOffset RequestedAt,
    IReadOnlyList<GoalProposalDto> Goals);
