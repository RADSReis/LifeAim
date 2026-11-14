namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>Loads and saves a conversation's state and history (the workflow's "checkpointer").</summary>
internal interface IConversationStore
{
    /// <summary>The user's conversation, or a new one at <see cref="Stage.Greeting"/> on first contact.</summary>
    Task<ConversationState> LoadAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The last <paramref name="count"/> messages, oldest first.</summary>
    Task<IReadOnlyList<ConversationTurn>> RecentMessagesAsync(Guid userId, int count, CancellationToken cancellationToken);

    /// <summary>Saves the new state and both messages of this turn, all or nothing.</summary>
    Task SaveTurnAsync(
        Guid userId,
        ConversationState state,
        string userMessage,
        string assistantMessage,
        Stage messageStage,
        CancellationToken cancellationToken);
}

internal sealed record ConversationState(Stage Stage, string? UserName);
