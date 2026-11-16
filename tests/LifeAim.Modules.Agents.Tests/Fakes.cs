using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Tests;

/// <summary>
/// A pretend AI: answers with fixed texts (the JSON a real model would return), one per call in order
/// (the last one repeats), and records what it was sent, so the tests run offline, for free and always
/// the same way. An answer can also be a tool call (<see cref="ToolCall"/>), as a real model asks for one.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly ChatMessage[] _answers;

    public FakeChatClient(params string[] answers) =>
        _answers = answers.Select(answer => new ChatMessage(ChatRole.Assistant, answer)).ToArray();

    public FakeChatClient(params ChatMessage[] answers) => _answers = answers;

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    /// <summary>The model's answer "call this tool with these arguments", which the tool loop executes.</summary>
    public static ChatMessage ToolCall(string name, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}", name, arguments)]);

    public static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(messages.ToList());
        var answer = _answers[Math.Min(Calls.Count - 1, _answers.Length - 1)];
        return Task.FromResult(new ChatResponse(answer));
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
            request.DueDate, request.StartValue, request.TargetValue, request.Unit, DateTimeOffset.UtcNow, request.UserPlan);
        Saved.Add(goal);
        return Task.FromResult(goal);
    }

    public Task<IReadOnlyList<GoalDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GoalDto>>(
            Saved.Where(g => g.UserId == userId && !Cancelled.Contains(g.Id)).ToList());

    /// <summary>The goals cancelled: they stay in <see cref="Saved"/>, as in the database, but leave the list.</summary>
    public List<Guid> Cancelled { get; } = [];

    public Task<bool> CancelAsync(Guid goalId, Guid userId, CancellationToken cancellationToken = default)
    {
        var active = Saved.Any(g => g.Id == goalId && g.UserId == userId) && !Cancelled.Contains(goalId);
        if (active)
        {
            Cancelled.Add(goalId);
        }

        return Task.FromResult(active);
    }

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
            Unit = request.Unit, UserPlan = request.UserPlan,
        };
        return Task.FromResult<GoalDto?>(Saved[index]);
    }

    /// <summary>Every plan Pippin saved: the goal and its routines, in order.</summary>
    public List<(Guid GoalId, IReadOnlyList<RoutineInput> Routines)> RoutinesReplaced { get; } = [];

    public Task<IReadOnlyList<RoutineDto>?> ReplaceRoutinesAsync(
        Guid goalId,
        Guid userId,
        IReadOnlyList<RoutineInput> routines,
        CancellationToken cancellationToken = default)
    {
        if (!Saved.Any(g => g.Id == goalId && g.UserId == userId))
        {
            return Task.FromResult<IReadOnlyList<RoutineDto>?>(null);
        }

        RoutinesReplaced.Add((goalId, routines));
        return Task.FromResult<IReadOnlyList<RoutineDto>?>(routines.Select(r => ToDto(goalId, r)).ToList());
    }

    public Task<IReadOnlyList<RoutineDto>> ListRoutinesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RoutineDto>>(
            RoutinesReplaced.GroupBy(r => r.GoalId).SelectMany(g => g.Last().Routines.Select(r => ToDto(g.Key, r))).ToList());

    private static RoutineDto ToDto(Guid goalId, RoutineInput r) =>
        new(Guid.CreateVersion7(), goalId, r.Activity, r.Frequency, r.Interval, r.DaysOfWeek ?? [], r.TimesPerPeriod,
            r.TimeOfDay, r.DayPeriod, r.Amount, r.Unit, r.DurationMinutes, r.Source, r.StartsOn, r.EndsOn, DateTimeOffset.UtcNow);

    /// <summary>A goal saved earlier, at <paramref name="createdAt"/> (only the name: not plannable yet).</summary>
    public GoalDto Add(Guid userId, string name, DateTimeOffset createdAt)
    {
        var goal = new GoalDto(Guid.CreateVersion7(), userId, name, null, null, null, null, null, null, createdAt);
        Saved.Add(goal);
        return goal;
    }
}

/// <summary>The Progress module's contract in memory: records what Pippin registered.</summary>
internal sealed class FakeProgressService : IProgressService
{
    public List<ProgressEntryDto> Entries { get; } = [];

    public Task<ProgressEntryDto> RecordAsync(RecordProgressRequest request, CancellationToken cancellationToken = default)
    {
        var entry = new ProgressEntryDto(
            Guid.CreateVersion7(), request.UserId, request.GoalId, request.OccurredOn, request.Amount, request.Unit,
            request.Note, DateTimeOffset.UtcNow);
        Entries.Add(entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<ProgressEntryDto>> ListByGoalAsync(
        Guid userId, Guid goalId, int limit = 50, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProgressEntryDto>>(
            Entries.Where(e => e.UserId == userId && e.GoalId == goalId)
                .OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.CreatedAt).Take(limit).ToList());
}
