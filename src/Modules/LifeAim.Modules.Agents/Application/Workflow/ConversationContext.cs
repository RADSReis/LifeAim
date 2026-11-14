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

    /// <summary>The most recent messages before this one, oldest first.</summary>
    public required IReadOnlyList<ConversationTurn> History { get; init; }

    public required Stage Stage { get; set; }

    /// <summary>The name the user told Pippin, once known.</summary>
    public string? UserName { get; set; }

    /// <summary>Goals understood but not confirmed yet, and the chosen period.</summary>
    public GoalsDraft? Draft { get; set; }
}

internal enum ConversationRole
{
    User,
    Assistant,
}

internal sealed record ConversationTurn(ConversationRole Role, string Content);
