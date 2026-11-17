using LifeAim.Modules.Agents.Application.Fitness;
using LifeAim.Modules.Fitness.Contracts;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.Logging;
using Quartz;

namespace LifeAim.Modules.Agents.Application.Reports;

/// <summary>
/// The monthly summary (Funcionalidade 4), run by Quartz every few minutes. When a month ends, from 10h of the 1st of
/// the next one, each user with a plan gets the summary of the month that ended (with the quarterly panorama when it
/// closed a quarter). reporting.summaries keeps one row per user per month, so a restart or a second run never sends
/// it twice. A user in the middle of something is tried again later, during the first week of the month.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class MonthlySummaryJob(
    IGoalService goals,
    IReportingService reporting,
    IConversationStore store,
    SummaryWriter writer,
    IFitnessJournal journal,
    PippinWorkflow workflow,
    TimeProvider timeProvider,
    ILogger<MonthlySummaryJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(MonthlySummaryJob), "reports");

    /// <summary>The summary goes from this hour of the 1st (inside the 9h–22h window).</summary>
    public const int SendFromHour = 10;

    /// <summary>After this day of the month, a summary not sent yet is no longer news: it is left out.</summary>
    public const int LastDayToSend = 7;

    public async Task Execute(IJobExecutionContext context) => await RunAsync(context.CancellationToken);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = GoalHorizon.Now(timeProvider);
        if (now.Day > LastDayToSend || now.Hour < SendFromHour || GoalHorizon.IsQuietTime(now))
        {
            return;
        }

        var month = DateOnly.FromDateTime(now).AddMonths(-1);
        foreach (var userId in await goals.ListUserIdsWithRoutinesAsync(cancellationToken))
        {
            try
            {
                await SummarizeAsync(userId, month, cancellationToken);
            }
            catch (AiNotConfiguredException)
            {
                logger.LogWarning("The monthly summary waits: OpenAI:ApiKey is not configured.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The monthly summary of user {UserId} failed.", userId);
            }
        }
    }

    /// <summary>True when the summary was sent now.</summary>
    internal async Task<bool> SummarizeAsync(Guid userId, DateOnly month, CancellationToken cancellationToken)
    {
        if (await reporting.GetForMonthAsync(userId, month, cancellationToken) is not null)
        {
            return false; // already sent (or nothing to say)
        }

        // Checked first so a busy user costs no AI call; checked again, under the lock, before sending.
        var state = await store.LoadAsync(userId, cancellationToken);
        if (!PippinWorkflow.CanSendSummary(state.Stage))
        {
            return false;
        }

        var report = await reporting.BuildMonthlyAsync(userId, month, cancellationToken);
        if (report.IsEmpty)
        {
            await reporting.RecordSkippedAsync(userId, month, cancellationToken);
            return false;
        }

        // The month of the weight-loss journal, for someone who keeps it.
        var monthStart = new DateOnly(month.Year, month.Month, 1);
        var days = await journal.ListDaysAsync(userId, monthStart, monthStart.AddMonths(1).AddDays(-1), cancellationToken);
        var journalFacts = FitnessPeriod.Summary(days, await journal.GetProfileAsync(userId, cancellationToken));

        var written = await writer.WriteAsync(report, state.UserName, cancellationToken, journalFacts);
        var summaryId = Guid.CreateVersion7();
        var sent = await workflow.SendSummaryAsync(
            userId,
            written.Text,
            written.InsightTeaser is null ? null : summaryId,
            ct => reporting.RecordSentAsync(
                new RecordSummaryRequest(
                    summaryId, userId, month, report.Quarter is not null, written.Message, written.InsightTeaser, written.Insight),
                ct),
            cancellationToken);

        if (sent)
        {
            logger.LogInformation("Monthly summary of {Month:yyyy-MM} sent to {UserId}.", month, userId);
        }

        return sent;
    }
}
