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

    /// <summary>Every goal "in the database": the ones a test put there plus the ones Pippin created.</summary>
    public List<GoalDto> Saved { get; } = [];

    public Task<GoalDto> CreateAsync(CreateGoalRequest request, CancellationToken cancellationToken = default)
    {
        Created.Add(request);
        var goal = new GoalDto(
            Guid.CreateVersion7(), request.UserId, request.Name, request.Description, request.Indicator,
            request.DueDate, request.StartValue, request.TargetValue, request.Unit, DateTimeOffset.UtcNow);
        Saved.Add(goal);
        return Task.FromResult(goal);
    }

    public Task<IReadOnlyList<GoalDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GoalDto>>(Saved.Where(g => g.UserId == userId).ToList());

    public List<UpdateGoalRequest> Updated { get; } = [];

    public Task<GoalDto?> UpdateAsync(UpdateGoalRequest request, CancellationToken cancellationToken = default)
    {
        Updated.Add(request);
        var index = Saved.FindIndex(g => g.Id == request.GoalId && g.UserId == request.UserId);
        if (index < 0)
        {
            return Task.FromResult<GoalDto?>(null);
        }

        Saved[index] = Saved[index] with
        {
            Name = request.Name, Description = request.Description, Indicator = request.Indicator,
            DueDate = request.DueDate, StartValue = request.StartValue, TargetValue = request.TargetValue,
            Unit = request.Unit,
        };
        return Task.FromResult<GoalDto?>(Saved[index]);
    }

    /// <summary>A goal saved earlier, at <paramref name="createdAt"/> (only the name: not plannable yet).</summary>
    public GoalDto Add(Guid userId, string name, DateTimeOffset createdAt)
    {
        var goal = new GoalDto(Guid.CreateVersion7(), userId, name, null, null, null, null, null, null, createdAt);
        Saved.Add(goal);
        return goal;
    }
}
