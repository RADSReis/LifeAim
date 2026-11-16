using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.Logging;
using Quartz;

namespace LifeAim.Modules.Agents.Application.FollowUp;

/// <summary>
/// The daily follow-up (Funcionalidade 3), run by Quartz every minute. Like the plan proposals job, the state lives
/// in the database (the plan in goals.goal_routines, what was asked and answered in progress.check_ins), so a restart
/// loses nothing. For each user with a plan: is something planned for now, or still unanswered? → the AI writes the
/// message → Pippin sends it and waits at Stage.CheckIn. Users in the middle of something are left for the next run.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class DailyFollowUpJob(
    IGoalService goals,
    IProgressService progress,
    ICheckInService checkIns,
    IConversationStore store,
    CheckInWriter writer,
    PippinWorkflow workflow,
    TimeProvider timeProvider,
    ILogger<DailyFollowUpJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(DailyFollowUpJob), "follow-up");

    /// <summary>How far back the planner and the writer look (check-ins and the history in the message).</summary>
    private const int HistoryDays = 14;

    public async Task Execute(IJobExecutionContext context) => await RunAsync(context.CancellationToken);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var userId in await goals.ListUserIdsWithRoutinesAsync(cancellationToken))
        {
            try
            {
                await FollowUpAsync(userId, cancellationToken);
            }
            catch (AiNotConfiguredException)
            {
                logger.LogWarning("The daily follow-up waits: OpenAI:ApiKey is not configured.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The daily follow-up of user {UserId} failed.", userId);
            }
        }
    }

    /// <summary>True when a message was sent.</summary>
    internal async Task<bool> FollowUpAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Checked first so a busy user costs no AI call; checked again, under the lock, before sending.
        var state = await store.LoadAsync(userId, cancellationToken);
        if (!PippinWorkflow.CanStartCheckIn(state.Stage))
        {
            return false;
        }

        var today = GoalHorizon.Today(timeProvider);
        var userGoals = await goals.ListAsync(userId, cancellationToken);
        var routines = await goals.ListRoutinesAsync(userId, cancellationToken);
        var recentCheckIns = await checkIns.ListAsync(userId, today.AddDays(-HistoryDays), today, cancellationToken);

        var entries = new List<ProgressEntryDto>();
        foreach (var goalId in routines.Select(r => r.GoalId).Distinct())
        {
            entries.AddRange(await progress.ListByGoalAsync(userId, goalId, 50, cancellationToken));
        }

        var input = new FollowUpInput(timeProvider.GetUtcNow(), userGoals, routines, recentCheckIns, entries);
        if (FollowUpPlanner.Decide(input) is not { } plan)
        {
            return false;
        }

        var text = await writer.WriteAsync(plan, input, state.UserName, cancellationToken);
        return await workflow.StartCheckInAsync(
            userId, text, ct => MarkAskedAsync(userId, plan, routines, userGoals, ct), cancellationToken);
    }

    private async Task<IReadOnlyList<CheckInItem>> MarkAskedAsync(
        Guid userId,
        FollowUpPlan plan,
        IReadOnlyList<RoutineDto> routines,
        IReadOnlyList<GoalDto> userGoals,
        CancellationToken cancellationToken)
    {
        var items = new List<CheckInItem>();

        foreach (var due in plan.Due)
        {
            var checkIn = await checkIns.MarkAskedAsync(
                new AskCheckInRequest(userId, due.Goal.Id, due.Routine.Id, plan.Today, due.Routine.Activity), cancellationToken);
            items.Add(Item(checkIn, due.Goal.Name, due.Routine));
        }

        foreach (var open in plan.PendingToday.Concat(plan.Earlier))
        {
            var checkIn = await checkIns.MarkAskedAsync(
                new AskCheckInRequest(userId, open.GoalId, open.RoutineId, open.Day, open.Activity), cancellationToken);
            var goalName = userGoals.FirstOrDefault(g => g.Id == open.GoalId)?.Name ?? open.Activity;
            items.Add(Item(checkIn, goalName, routines.FirstOrDefault(r => r.Id == open.RoutineId)));
        }

        return items;
    }

    private static CheckInItem Item(CheckInDto checkIn, string goalName, RoutineDto? routine) =>
        new(checkIn.Id, checkIn.GoalId, goalName, checkIn.Activity, checkIn.Day, routine?.Amount, routine?.Unit);
}
