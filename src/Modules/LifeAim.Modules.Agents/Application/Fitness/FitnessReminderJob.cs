using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using LifeAim.Modules.Fitness.Contracts;
using Microsoft.Extensions.Logging;
using Quartz;

namespace LifeAim.Modules.Agents.Application.Fitness;

/// <summary>
/// The journal's reminders, run by Quartz every few minutes. The state is in the database (the reminders and what was
/// sent on each day), so the job only polls and a restart sends nothing twice. For each user with reminders: is one
/// due now (<see cref="FitnessReminderPlanner"/>)? → write it → Pippin sends it. The conversation's stage does not
/// change: a reminder asks for nothing the workflow has to wait for, and the answer ("94,3 kg", "tomei") is an
/// ordinary message of the journal. Users in the middle of a cadastro are left for the next run.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class FitnessReminderJob(
    IFitnessReminders reminders,
    IFitnessJournal journal,
    IConversationStore store,
    FitnessReminderWriter writer,
    FitnessAgent agent,
    PippinWorkflow workflow,
    TimeProvider timeProvider,
    ILogger<FitnessReminderJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(FitnessReminderJob), "fitness");

    public async Task Execute(IJobExecutionContext context) => await RunAsync(context.CancellationToken);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var userId in await reminders.ListUserIdsAsync(cancellationToken))
        {
            try
            {
                await RemindAsync(userId, cancellationToken);
            }
            catch (AiNotConfiguredException)
            {
                logger.LogWarning("The summary of the day waits: OpenAI:ApiKey is not configured.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The journal reminders of user {UserId} failed.", userId);
            }
        }
    }

    /// <summary>True when a message was sent.</summary>
    internal async Task<bool> RemindAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Checked first so a busy user costs nothing; checked again, under the lock, before sending.
        var state = await store.LoadAsync(userId, cancellationToken);
        if (!PippinWorkflow.CanSendReminder(state.Stage))
        {
            return false;
        }

        // With the goal that opens the journal paused or ended, its reminders stop (and come back with it).
        if (!await agent.IsActiveAsync(userId, null, cancellationToken))
        {
            return false;
        }

        var now = GoalHorizon.Now(timeProvider);
        var today = FoodDay.Of(now);
        var day = await journal.GetDayAsync(userId, today, cancellationToken);
        var due = FitnessReminderPlanner.Due(now, await reminders.ListAsync(userId, today, cancellationToken), day);
        if (due.Count == 0)
        {
            return false;
        }

        var recent = await journal.ListDaysAsync(userId, today.AddDays(-FitnessAgent.RecentDays), today, cancellationToken);
        var profile = await journal.GetProfileAsync(userId, cancellationToken);
        var text = await writer.WriteAsync(due, day, recent, profile, state.UserName, cancellationToken);

        return await workflow.SendNoteAsync(
            userId,
            text,
            async ct =>
            {
                foreach (var reminder in due)
                {
                    await reminders.MarkSentAsync(reminder.Id, userId, today, ct);
                }
            },
            cancellationToken);
    }
}
