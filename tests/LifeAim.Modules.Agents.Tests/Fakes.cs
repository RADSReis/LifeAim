using LifeAim.Modules.Agents.Application.Workflow;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>
/// A pretend AI: answers with a fixed text (the JSON a real model would return) and records what it was
/// sent, so the tests run offline, for free and always the same way.
/// </summary>
internal sealed class FakeChatClient(string answer) : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(messages.ToList());
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>The conversation store in memory, instead of PostgreSQL.</summary>
internal sealed class InMemoryConversationStore : IConversationStore
{
    public Dictionary<Guid, ConversationState> States { get; } = [];
    public List<(Guid UserId, ConversationTurn Turn, Stage Stage)> Messages { get; } = [];

    public Task<ConversationState> LoadAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(States.GetValueOrDefault(userId) ?? new ConversationState(Stage.Greeting, null));

    public Task<IReadOnlyList<ConversationTurn>> RecentMessagesAsync(Guid userId, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConversationTurn>>(
            Messages.Where(m => m.UserId == userId).Select(m => m.Turn).TakeLast(count).ToList());

    public Task SaveTurnAsync(
        Guid userId,
        ConversationState state,
        string userMessage,
        string assistantMessage,
        Stage messageStage,
        CancellationToken cancellationToken)
    {
        States[userId] = state;
        Messages.Add((userId, new ConversationTurn(ConversationRole.User, userMessage), messageStage));
        Messages.Add((userId, new ConversationTurn(ConversationRole.Assistant, assistantMessage), messageStage));
        return Task.CompletedTask;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
