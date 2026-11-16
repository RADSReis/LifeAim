using System.Text;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.Logging;
using Quartz;

namespace LifeAim.Modules.Agents.Application.Pauses;

/// <summary>
/// Funcionalidade 6, run by Quartz every 30 minutes: when the pause of a goal reaches the day the user chose, Pippin
/// asks once whether it comes back (goals.goals.resume_offered_at keeps it from asking twice). During the day, only
/// with the user between tasks, one goal at a time; the goal stays paused until the user says yes.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class PausedGoalsJob(
    IGoalService goals,
    IConversationStore store,
    PippinWorkflow workflow,
    TimeProvider timeProvider,
    ILogger<PausedGoalsJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(PausedGoalsJob), "goals");

    public const int SendFromHour = 10;
    public const int SendUntilHour = 20;

    public async Task Execute(IJobExecutionContext context) => await RunAsync(context.CancellationToken);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = GoalHorizon.Now(timeProvider);
        if (now.Hour < SendFromHour || now.Hour >= SendUntilHour)
        {
            return;
        }

        var ending = await goals.ListPausesEndingAsync(GoalHorizon.Today(timeProvider), cancellationToken);
        foreach (var goal in ending.GroupBy(g => g.UserId).Select(g => g.First()))
        {
            try
            {
                await OfferAsync(goal, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Asking whether goal {GoalId} comes back failed.", goal.Id);
            }
        }
    }

    /// <summary>True when Pippin asked now.</summary>
    internal async Task<bool> OfferAsync(GoalDto goal, CancellationToken cancellationToken)
    {
        var state = await store.LoadAsync(goal.UserId, cancellationToken);
        if (!PippinWorkflow.CanOfferResume(state.Stage))
        {
            return false;
        }

        var routines = (await goals.ListGoalRoutinesAsync(goal.Id, goal.UserId, cancellationToken)).Count;
        var text = Message(goal, state.UserName, routines > 0);
        var sent = await workflow.OfferResumeAsync(
            goal.UserId, goal.Id, text, ct => goals.MarkResumeOfferedAsync(goal.Id, goal.UserId, ct), cancellationToken);

        if (sent)
        {
            logger.LogInformation("Pippin asked whether paused goal {GoalId} comes back.", goal.Id);
        }

        return sent;
    }

    /// <summary>Plain code: a short, warm question needs no AI call.</summary>
    internal static string Message(GoalDto goal, string? userName, bool hasPlan)
    {
        var text = new StringBuilder(userName is null ? "Oi! " : $"Oi, {userName}! ")
            .Append("Hoje termina a pausa que você deu em **").Append(goal.Name).Append("**. 🌱 ")
            .Append(hasPlan ? "O plano dela continua guardado do jeito que estava. " : "")
            .Append("\n\n").Append(ResumeGoalNode.Question(goal.Name));
        return text.ToString();
    }
}
