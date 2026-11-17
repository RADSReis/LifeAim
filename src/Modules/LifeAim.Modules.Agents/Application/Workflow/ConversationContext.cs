using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;

namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// The state every node reads and writes during one message: loaded from the database before the
/// node runs, saved after. (LangGraph calls this the graph "state".)
/// </summary>
internal sealed class ConversationContext
{
    public required Guid UserId { get; init; }

    /// <summary>How the channel calls the user (their Discord name).</summary>
    public required string DisplayName { get; init; }

    /// <summary>What the user just wrote.</summary>
    public required string IncomingText { get; init; }

    /// <summary>Photos sent with this message; only the fitness agent looks at them.</summary>
    public IReadOnlyList<PippinImage> Images { get; init; } = [];

    /// <summary>The most recent messages before this one, oldest first.</summary>
    public required IReadOnlyList<ConversationTurn> History { get; init; }

    public required Stage Stage { get; set; }

    /// <summary>The name the user told Pippin, once known.</summary>
    public string? UserName { get; set; }

    /// <summary>Goals understood but not confirmed yet, and the chosen period.</summary>
    public GoalsDraft? Draft { get; set; }

    /// <summary>The user's goals already saved, read from the Goals module at the start of the message.</summary>
    public IReadOnlyList<GoalDto> SavedGoals { get; init; } = [];

    /// <summary>
    /// The user has an active goal with the fitness specialty (about weight or training): the weight-loss journal is
    /// open to them.
    /// </summary>
    public bool FitnessActive { get; init; }

    /// <summary>The user's paused goals (Funcionalidade 6), oldest first: out of the lists, but they can come back.</summary>
    public IReadOnlyList<GoalDto> PausedGoals { get; init; } = [];
}

internal enum ConversationRole
{
    User,
    Assistant,
}

internal sealed record ConversationTurn(ConversationRole Role, string Content);
