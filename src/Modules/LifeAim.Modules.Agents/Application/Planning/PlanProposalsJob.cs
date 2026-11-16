using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using Microsoft.Extensions.Logging;
using Quartz;

namespace LifeAim.Modules.Agents.Application.Planning;

/// <summary>
/// The background part of Funcionalidade 2, run by Quartz every few seconds. The work to do lives in the database
/// (planning.proposals by status), not in Quartz, so a restart loses nothing and Quartz can keep its jobs in memory:
/// 1. requested → the AI writes the proposal → ready;
/// 2. ready → outside the quiet hours and when the user is not in the middle of something, Pippin sends the
///    invitation in the chat → offered.
/// [DisallowConcurrentExecution]: a slow AI call never makes two runs handle the same proposal at once.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class PlanProposalsJob(
    IPlanningService planning,
    IGoalService goals,
    PlanProposer proposer,
    PippinWorkflow workflow,
    TimeProvider timeProvider,
    ILogger<PlanProposalsJob> logger) : IJob
{
    public static readonly JobKey Key = new(nameof(PlanProposalsJob), "planning");

    public async Task Execute(IJobExecutionContext context) => await RunAsync(context.CancellationToken);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var proposal in await planning.ListByStatusAsync(ProposalStatus.Requested, cancellationToken: cancellationToken))
        {
            await PrepareAsync(proposal, cancellationToken);
        }

        if (GoalHorizon.IsQuietTime(timeProvider))
        {
            return; // the invitations wait for 9h
        }

        foreach (var proposal in await planning.ListByStatusAsync(ProposalStatus.Ready, cancellationToken: cancellationToken))
        {
            if (await workflow.OfferPlanAsync(proposal.UserId, proposal.Id, cancellationToken))
            {
                await planning.MarkOfferedAsync(proposal.Id, cancellationToken);
            }
        }
    }

    private async Task PrepareAsync(PlanProposalDto proposal, CancellationToken cancellationToken)
    {
        try
        {
            // Goals already agreed on in an earlier proposal are left out; the plan the user described is kept as input.
            var accepted = await planning.AcceptedGoalIdsAsync(proposal.UserId, cancellationToken);
            var pending = (await goals.ListAsync(proposal.UserId, cancellationToken))
                .Where(g => !accepted.Contains(g.Id))
                .ToList();
            var routines = await goals.ListRoutinesAsync(proposal.UserId, cancellationToken);

            var written = await proposer.ProposeAsync(pending, routines, cancellationToken);
            if (pending.Count > 0 && written.Count == 0)
            {
                throw new InvalidOperationException("The AI wrote no usable proposal for any goal.");
            }

            await planning.MarkReadyAsync(proposal.Id, written, cancellationToken);
            logger.LogInformation("Plan proposal {ProposalId} ready with {Count} goals.", proposal.Id, written.Count);
        }
        catch (AiNotConfiguredException)
        {
            logger.LogWarning("Plan proposal {ProposalId} waits: OpenAI:ApiKey is not configured.", proposal.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Plan proposal {ProposalId} failed (attempt {Attempt}).", proposal.Id, proposal.Attempts + 1);
            await planning.MarkFailedAttemptAsync(proposal.Id, ex.Message, cancellationToken);
        }
    }
}
