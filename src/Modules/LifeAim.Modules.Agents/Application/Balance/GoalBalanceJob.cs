using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.Logging;
using Quartz;

namespace LifeAim.Modules.Agents.Application.Balance;

/// <summary>
/// Priorities and conflicts between goals (Funcionalidade 5), run by Quartz every 30 minutes. The rules are in the
/// Reporting module (<see cref="IBalanceService.AnalyzeAsync"/>), in plain code; the job only decides whether this is
/// a good moment: during the day, with the user between tasks, and not again within
/// <see cref="BalanceRules.CooldownDays"/> days of the last review ("evitar intervenções desnecessárias"). When the
/// history shows a signal, the AI writes the message and Pippin waits at <see cref="Stage.BalanceReview"/>.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class GoalBalanceJob(
    IGoalService goals,
    IBalanceService balance,
    IConversationStore store,
    BalanceWriter writer,
    PippinWorkflow workflow,
    TimeProvider timeProvider,
    ILogger<GoalBalanceJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(GoalBalanceJob), "balance");

    /// <summary>A conversation like this one fits the day, not the start of the morning nor the end of the night.</summary>
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

        foreach (var userId in await goals.ListUserIdsWithRoutinesAsync(cancellationToken))
        {
            try
            {
                await ReviewAsync(userId, cancellationToken);
            }
            catch (AiNotConfiguredException)
            {
                logger.LogWarning("The priorities review waits: OpenAI:ApiKey is not configured.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The priorities review of user {UserId} failed.", userId);
            }
        }
    }

    /// <summary>True when a review was sent now.</summary>
    internal async Task<bool> ReviewAsync(Guid userId, CancellationToken cancellationToken)
    {
        var state = await store.LoadAsync(userId, cancellationToken);
        if (!PippinWorkflow.CanStartBalanceReview(state.Stage))
        {
            return false;
        }

        var latest = await balance.GetLatestAsync(userId, cancellationToken);
        if (latest is not null && timeProvider.GetUtcNow() - latest.CreatedAt < TimeSpan.FromDays(BalanceRules.CooldownDays))
        {
            return false;
        }

        var today = GoalHorizon.Today(timeProvider);
        var analysis = await balance.AnalyzeAsync(userId, today, cancellationToken);
        if (analysis is not { FocusGoalId: { } focusId })
        {
            return false;
        }

        // The user said "prefiro manter" about this same goal: twice the wait before asking about it again.
        if (latest is { Status: ReviewStatus.Declined } && latest.FocusGoalId == focusId
            && timeProvider.GetUtcNow() - latest.CreatedAt < TimeSpan.FromDays(2 * BalanceRules.CooldownDays))
        {
            return false;
        }

        var routines = await goals.ListRoutinesAsync(userId, cancellationToken);
        var text = await writer.WriteAsync(analysis, routines, state.UserName, cancellationToken);
        var reviewId = Guid.CreateVersion7();

        var sent = await workflow.StartBalanceReviewAsync(
            userId,
            text,
            reviewId,
            BalanceWriter.Question(analysis.Focus!.Name),
            ct => balance.RecordSentAsync(
                new RecordReviewRequest(
                    reviewId, userId, focusId, analysis.Signals.Select(s => s.Kind).ToList(),
                    analysis.Thriving.Select(g => g.GoalId).ToList(), text),
                ct),
            cancellationToken);

        if (sent)
        {
            logger.LogInformation(
                "Priorities review sent to {UserId} about goal {GoalId} ({Signals}).",
                userId, focusId, string.Join(", ", analysis.Signals.Select(s => s.Kind)));
        }

        return sent;
    }
}
