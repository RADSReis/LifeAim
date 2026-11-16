using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;
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

    public Task SaveProactiveAsync(
        Guid userId,
        ConversationState state,
        string assistantMessage,
        Stage messageStage,
        CancellationToken cancellationToken)
    {
        States[userId] = state;
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
    public List<(Guid GoalId, IReadOnlyList<RoutineInput> Routines, IReadOnlyList<RoutineDto> Dtos)> RoutinesReplaced { get; } = [];

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

        var dtos = routines.Select(r => ToDto(goalId, r)).ToList();
        RoutinesReplaced.Add((goalId, routines, dtos));
        return Task.FromResult<IReadOnlyList<RoutineDto>?>(dtos);
    }

    public Task<IReadOnlyList<RoutineDto>> ListRoutinesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RoutineDto>>(
            RoutinesReplaced.GroupBy(r => r.GoalId).SelectMany(g => g.Last().Dtos).ToList());

    public Task<IReadOnlyList<Guid>> ListUserIdsWithRoutinesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(Saved
            .Where(g => !Cancelled.Contains(g.Id) && RoutinesReplaced.Any(r => r.GoalId == g.Id && r.Routines.Count > 0))
            .Select(g => g.UserId).Distinct().ToList());

    /// <summary>Routines created at a fixed moment (their start, when they have no StartsOn), for the follow-up tests.</summary>
    public DateTimeOffset RoutinesCreatedAt { get; set; } = DateTimeOffset.UtcNow;

    private RoutineDto ToDto(Guid goalId, RoutineInput r) =>
        new(Guid.CreateVersion7(), goalId, r.Activity, r.Frequency, r.Interval, r.DaysOfWeek ?? [], r.TimesPerPeriod,
            r.TimeOfDay, r.DayPeriod, r.Amount, r.Unit, r.DurationMinutes, r.Source, r.StartsOn, r.EndsOn, RoutinesCreatedAt);

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

    public Task<IReadOnlyList<ProgressEntryDto>> ListAsync(
        Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProgressEntryDto>>(
            Entries.Where(e => e.UserId == userId && e.OccurredOn >= from && e.OccurredOn <= to).OrderBy(e => e.OccurredOn).ToList());
}

/// <summary>The Planning module's contract in memory, with the same status rules as the database.</summary>
internal sealed class FakePlanningService : IPlanningService
{
    public List<PlanProposalDto> Proposals { get; } = [];

    public Task<bool> RequestAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (Proposals.Any(p => p.UserId == userId && p.Status == ProposalStatus.Requested))
        {
            return Task.FromResult(false);
        }

        foreach (var open in Proposals.Where(p => p.UserId == userId && p.Status is ProposalStatus.Ready or ProposalStatus.Offered).ToList())
        {
            Set(open.Id, p => p with { Status = ProposalStatus.Closed });
        }

        Proposals.Add(new PlanProposalDto(Guid.CreateVersion7(), userId, ProposalStatus.Requested, 0, DateTimeOffset.UtcNow, []));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PlanProposalDto>> ListByStatusAsync(
        ProposalStatus status, int limit = 20, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlanProposalDto>>(Proposals.Where(p => p.Status == status).Take(limit).ToList());

    public Task<PlanProposalDto?> GetOpenAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Proposals.FirstOrDefault(p => p.UserId == userId && p.Status is ProposalStatus.Ready or ProposalStatus.Offered));

    public Task<IReadOnlyList<Guid>> AcceptedGoalIdsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(Proposals.Where(p => p.UserId == userId)
            .SelectMany(p => p.Goals).Where(g => g.Status == GoalProposalStatus.Accepted).Select(g => g.GoalId).ToList());

    public Task MarkReadyAsync(Guid proposalId, IReadOnlyList<GoalProposalInput> goals, CancellationToken cancellationToken = default)
    {
        var items = goals.Select((g, i) => new GoalProposalDto(g.GoalId, i + 1, g.Message, g.Question, g.Routines, GoalProposalStatus.Open)).ToList();
        Set(proposalId, p => p with { Status = items.Count == 0 ? ProposalStatus.Closed : ProposalStatus.Ready, Goals = items });
        return Task.CompletedTask;
    }

    public Task MarkFailedAttemptAsync(Guid proposalId, string error, CancellationToken cancellationToken = default)
    {
        Set(proposalId, p => p with
        {
            Attempts = p.Attempts + 1,
            Status = p.Attempts + 1 >= IPlanningService.MaxAttempts ? ProposalStatus.Failed : p.Status,
        });
        return Task.CompletedTask;
    }

    public Task MarkOfferedAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        Set(proposalId, p => p with { Status = ProposalStatus.Offered });
        return Task.CompletedTask;
    }

    public Task SetGoalStatusAsync(Guid proposalId, Guid goalId, GoalProposalStatus status, CancellationToken cancellationToken = default)
    {
        Set(proposalId, p => p with { Goals = p.Goals.Select(g => g.GoalId == goalId ? g with { Status = status } : g).ToList() });
        return Task.CompletedTask;
    }

    public Task CloseAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        Set(proposalId, p => p with { Status = ProposalStatus.Closed });
        return Task.CompletedTask;
    }

    /// <summary>A proposal already written, as the job leaves it (ready to offer).</summary>
    public PlanProposalDto AddReady(Guid userId, params GoalProposalInput[] goals)
    {
        var proposal = new PlanProposalDto(
            Guid.CreateVersion7(), userId, ProposalStatus.Ready, 0, DateTimeOffset.UtcNow,
            goals.Select((g, i) => new GoalProposalDto(g.GoalId, i + 1, g.Message, g.Question, g.Routines, GoalProposalStatus.Open)).ToList());
        Proposals.Add(proposal);
        return proposal;
    }

    private void Set(Guid id, Func<PlanProposalDto, PlanProposalDto> change)
    {
        var index = Proposals.FindIndex(p => p.Id == id);
        Proposals[index] = change(Proposals[index]);
    }
}

/// <summary>The channel for messages Pippin starts himself: records what would be sent.</summary>
internal sealed class FakePippinChannel : IPippinChannel
{
    public bool Delivers { get; set; } = true;

    public List<(Guid UserId, string Text)> Sent { get; } = [];

    public Task<bool> SendAsync(Guid userId, string text, CancellationToken cancellationToken = default)
    {
        if (Delivers)
        {
            Sent.Add((userId, text));
        }

        return Task.FromResult(Delivers);
    }
}

/// <summary>The check-ins in memory, with the same rules as progress.check_ins (one per activity per day).</summary>
internal sealed class FakeCheckInService(TimeProvider? time = null) : ICheckInService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public List<CheckInDto> CheckIns { get; } = [];

    public Task<IReadOnlyList<CheckInDto>> ListAsync(
        Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CheckInDto>>(
            CheckIns.Where(c => c.UserId == userId && c.Day >= from && c.Day <= to).OrderBy(c => c.Day).ToList());

    public Task<CheckInDto> MarkAskedAsync(AskCheckInRequest request, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var index = CheckIns.FindIndex(c => c.RoutineId == request.RoutineId && c.Day == request.Day);
        if (index < 0)
        {
            CheckIns.Add(new CheckInDto(
                Guid.CreateVersion7(), request.UserId, request.GoalId, request.RoutineId, request.Day, request.Activity,
                CheckInStatus.Asked, 1, now, now, null, null));
            return Task.FromResult(CheckIns[^1]);
        }

        if (CheckIns[index].Status == CheckInStatus.Asked)
        {
            CheckIns[index] = CheckIns[index] with { TimesAsked = CheckIns[index].TimesAsked + 1, LastAskedAt = now };
        }

        return Task.FromResult(CheckIns[index]);
    }

    public Task<bool> MarkDoneAsync(Guid checkInId, Guid userId, Guid? progressEntryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Answer(c => c.Id == checkInId && c.UserId == userId, CheckInStatus.Done, null) == 1);

    public Task<bool> MarkMissedAsync(Guid checkInId, Guid userId, string? reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(Answer(c => c.Id == checkInId && c.UserId == userId, CheckInStatus.Missed, reason) == 1);

    public Task<int> MarkDoneForGoalAsync(
        Guid userId, Guid goalId, DateOnly day, Guid? progressEntryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Answer(c => c.UserId == userId && c.GoalId == goalId && c.Day == day, CheckInStatus.Done, null));

    /// <summary>A check-in already in the "database", e.g. asked earlier and never answered.</summary>
    public CheckInDto Add(Guid userId, Guid goalId, Guid routineId, DateOnly day, string activity, DateTimeOffset askedAt,
        CheckInStatus status = CheckInStatus.Asked, int timesAsked = 1)
    {
        var checkIn = new CheckInDto(
            Guid.CreateVersion7(), userId, goalId, routineId, day, activity, status, timesAsked, askedAt, askedAt,
            status == CheckInStatus.Asked ? null : askedAt, null);
        CheckIns.Add(checkIn);
        return checkIn;
    }

    private int Answer(Func<CheckInDto, bool> which, CheckInStatus status, string? reason)
    {
        var count = 0;
        for (var i = 0; i < CheckIns.Count; i++)
        {
            if (which(CheckIns[i]) && CheckIns[i].Status == CheckInStatus.Asked)
            {
                CheckIns[i] = CheckIns[i] with { Status = status, Reason = reason, AnsweredAt = _time.GetUtcNow() };
                count++;
            }
        }

        return count;
    }
}

/// <summary>The Reporting module's contract in memory: a fixed report, and the summaries recorded (one per month).</summary>
internal sealed class FakeReportingService : IReportingService
{
    public MonthlyReportDto? Report { get; set; }

    public List<SummaryDto> Summaries { get; } = [];

    public List<DateOnly> Built { get; } = [];

    public Task<MonthlyReportDto> BuildMonthlyAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default)
    {
        Built.Add(month);
        return Task.FromResult(Report ?? throw new InvalidOperationException("The test set no report."));
    }

    public Task<SummaryDto?> GetForMonthAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default) =>
        Task.FromResult(Summaries.FirstOrDefault(s => s.UserId == userId && s.Month == new DateOnly(month.Year, month.Month, 1)));

    public Task<SummaryDto?> FindAsync(Guid summaryId, Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Summaries.FirstOrDefault(s => s.Id == summaryId && s.UserId == userId));

    public async Task<bool> RecordSentAsync(RecordSummaryRequest request, CancellationToken cancellationToken = default)
    {
        if (await GetForMonthAsync(request.UserId, request.Month, cancellationToken) is not null)
        {
            return false;
        }

        var insight = request.InsightTeaser is null || request.Insight is null ? null : request.Insight;
        Summaries.Add(new SummaryDto(
            request.SummaryId, request.UserId, new DateOnly(request.Month.Year, request.Month.Month, 1), SummaryStatus.Sent,
            request.IncludesQuarter, request.Message, insight is null ? null : request.InsightTeaser, insight,
            insight is null ? InsightStatus.None : InsightStatus.Offered, DateTimeOffset.UtcNow));
        return true;
    }

    public async Task<bool> RecordSkippedAsync(Guid userId, DateOnly month, CancellationToken cancellationToken = default)
    {
        if (await GetForMonthAsync(userId, month, cancellationToken) is not null)
        {
            return false;
        }

        Summaries.Add(new SummaryDto(
            Guid.CreateVersion7(), userId, new DateOnly(month.Year, month.Month, 1), SummaryStatus.Skipped, false, null, null,
            null, InsightStatus.None, DateTimeOffset.UtcNow));
        return true;
    }

    public Task<bool> AnswerInsightAsync(Guid summaryId, Guid userId, bool accepted, CancellationToken cancellationToken = default)
    {
        var index = Summaries.FindIndex(s => s.Id == summaryId && s.UserId == userId && s.InsightStatus == InsightStatus.Offered);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        Summaries[index] = Summaries[index] with { InsightStatus = accepted ? InsightStatus.Revealed : InsightStatus.Declined };
        return Task.FromResult(true);
    }
}
