using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>
/// A pretend AI: answers with fixed texts (the JSON a real model would return), one per call in order
/// (the last one repeats), and records what it was sent, so the tests run offline, for free and always
/// the same way.
/// </summary>
internal sealed class FakeChatClient(params string[] answers) : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(messages.ToList());
        var answer = answers[Math.Min(Calls.Count - 1, answers.Length - 1)];
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

    public Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        var removed = States.Remove(userId) | Messages.RemoveAll(m => m.UserId == userId) > 0;
        return Task.FromResult(removed);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>The Goals module's contract in memory: records what Pippin asked to save.</summary>
internal sealed class FakeGoalService : IGoalService
{
    public List<CreateGoalRequest> Created { get; } = [];

    public Task<GoalDto> CreateAsync(CreateGoalRequest request, CancellationToken cancellationToken = default)
    {
        Created.Add(request);
        return Task.FromResult(new GoalDto(
            Guid.CreateVersion7(), request.UserId, request.Name, request.Description, request.Indicator,
            request.DueDate, request.StartValue, request.TargetValue, request.Unit, DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<GoalDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
