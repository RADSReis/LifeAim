using System.Collections.Concurrent;
using LifeAim.Modules.Agents.Application.Agent;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.Logging;

namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// Pippin's hand-made workflow engine, run once per incoming message:
/// load the context → run the node of the current stage → if the message was not expected, hand it
/// to the path classifier (another stage, the goals agent, or the router) → follow the edge (next stage) →
/// save the context and both messages → reply.
/// </summary>
internal sealed class PippinWorkflow : IPippin
{
    /// <summary>How many past messages each node gets; the rest stays in the database only.</summary>
    private const int HistorySize = 20;

    private const string NoBrainReply =
        "Estou sem o meu cérebro de IA configurado agora 🧠🔌. Assim que ele for ligado, eu continuo daqui!";

    // Two messages from the same user arriving together would both load the same state and the second
    // save would overwrite the first. One at a time per user (fine while there is a single API instance).
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> UserLocks = new();

    private readonly IConversationStore _store;
    private readonly FallbackRouter _router;
    private readonly GreetingReply _greeting;
    private readonly PathClassifier _classifier;
    private readonly GoalsAgent _agent;
    private readonly IGoalService _goals;
    private readonly IPippinChannel _channel;
    private readonly ILogger<PippinWorkflow> _logger;
    private readonly Dictionary<Stage, INode> _nodes;

    public PippinWorkflow(
        IConversationStore store,
        IEnumerable<INode> nodes,
        FallbackRouter router,
        GreetingReply greeting,
        PathClassifier classifier,
        GoalsAgent agent,
        IGoalService goals,
        IPippinChannel channel,
        ILogger<PippinWorkflow> logger)
    {
        _channel = channel;
        _store = store;
        _router = router;
        _greeting = greeting;
        _classifier = classifier;
        _agent = agent;
        _goals = goals;
        _logger = logger;
        _nodes = nodes.ToDictionary(node => node.Stage);
    }

    public async Task<string> ReplyAsync(PippinMessage message, CancellationToken cancellationToken = default)
    {
        var userLock = UserLocks.GetOrAdd(message.UserId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken);
        try
        {
            // 1. Load the context (the "checkpoint").
            var state = await _store.LoadAsync(message.UserId, cancellationToken);
            var context = new ConversationContext
            {
                UserId = message.UserId,
                DisplayName = message.DisplayName,
                IncomingText = message.Text,
                History = await _store.RecentMessagesAsync(message.UserId, HistorySize, cancellationToken),
                Stage = state.Stage,
                UserName = state.UserName,
                Draft = state.Draft,
                SavedGoals = state.Stage == Stage.Greeting ? [] : await _goals.ListAsync(message.UserId, cancellationToken),
            };
            var stageOfThisMessage = context.Stage;

            // 2. Run the node of the current stage; 3. fall back to the router if it did not expect this.
            var result = await RunAsync(context, cancellationToken);
            var reply = result.Reply ?? NoBrainReply;

            // 4. Follow the edge.
            if (result.NextStage is { } next)
            {
                _logger.LogInformation("Pippin workflow: {From} → {To}", context.Stage, next);
                context.Stage = next;
            }

            // Back to something in progress (a cancellation asked in the middle of a detailing): its question again.
            if (result.Resumes)
            {
                reply = WithPendingQuestion(reply, context);
            }

            // 5. Save the context and the turn.
            await _store.SaveTurnAsync(
                message.UserId,
                new ConversationState(context.Stage, context.UserName, context.Draft),
                message.Text,
                reply,
                stageOfThisMessage,
                cancellationToken);

            return reply;
        }
        finally
        {
            userLock.Release();
        }
    }

    /// <summary>
    /// Pippin starts the conversation himself: the plan proposal is ready (Funcionalidade 2). Only when the user is
    /// not in the middle of something (CollectGoals, between tasks); otherwise false, and the job tries again later.
    /// Runs under the same per-user lock as the replies, so it never overwrites a message being answered.
    /// </summary>
    public async Task<bool> OfferPlanAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken)
    {
        var userLock = UserLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken);
        try
        {
            var state = await _store.LoadAsync(userId, cancellationToken);
            if (state.Stage != Stage.CollectGoals)
            {
                return false;
            }

            if (!await _channel.SendAsync(userId, PlanReview.Invitation, cancellationToken))
            {
                return false;
            }

            var draft = GoalsDraft.Empty(state.Draft?.Horizon) with { ProposalId = proposalId };
            await _store.SaveProactiveAsync(
                userId, state with { Stage = Stage.OfferPlan, Draft = draft }, PlanReview.Invitation, state.Stage, cancellationToken);
            _logger.LogInformation("Pippin offered plan proposal {ProposalId}: {From} → {To}", proposalId, state.Stage, Stage.OfferPlan);
            return true;
        }
        finally
        {
            userLock.Release();
        }
    }

    private async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var node = _nodes[context.Stage];
            var result = await node.RunAsync(context, cancellationToken);
            if (result.Expected)
            {
                return result;
            }

            // The node already acted and moved on (an ignored confirmation taken as "sim"): the message is
            // handled from the new stage, by its node first ("quero adicionar outra meta" → CollectGoals), and
            // the reply starts with what was done. Only once: a second move is treated as not understood.
            var note = result.Note;
            if (result.NextStage is { } moved)
            {
                _logger.LogInformation("Pippin workflow: {From} → {To} before handling the message", context.Stage, moved);
                context.Stage = moved;
                node = _nodes[moved];

                result = await node.RunAsync(context, cancellationToken);
                if (result.Expected)
                {
                    return NodeResult.Answer($"{note}\n\n{result.Reply}", result.NextStage);
                }
            }

            // A greeting asks for nothing: greet back. Anything else unexpected → the path classifier decides.
            var handled = result.IsGreeting
                ? NodeResult.Answer(_greeting.Reply(context, node.PendingQuestion(context)))
                : await FindPathAsync(context, node, cancellationToken);

            return note is null
                ? handled
                : NodeResult.Answer($"{note}\n\n{handled.Reply}", handled.NextStage);
        }
        catch (AiNotConfiguredException)
        {
            _logger.LogWarning("Pippin needs the AI at stage {Stage}, but OpenAI:ApiKey is not configured.", context.Stage);
            return NodeResult.Answer(NoBrainReply);
        }
    }

    // The node did not understand: the classifier picks a way on, the code checks and applies it.
    // At most one jump per message: if the destination does not understand either, Pippin asks again.
    private async Task<NodeResult> FindPathAsync(ConversationContext context, INode node, CancellationToken cancellationToken)
    {
        var pending = node.PendingQuestion(context);
        var decision = await _classifier.ClassifyAsync(context, pending, cancellationToken);
        _logger.LogInformation(
            "Pippin path at {Stage}: {Kind} {Target} ({Reason})", context.Stage, decision.Kind, decision.Stage, decision.Reason);

        switch (decision.Kind)
        {
            case PathKind.Goto when decision.Stage is { } target && PathClassifier.IsAllowed(context.Stage, target):
                var (stageBefore, draftBefore) = (context.Stage, context.Draft);
                context.Stage = target;
                var entered = await _nodes[target].EnterAsync(context, decision.GoalId, cancellationToken);
                if (entered.Expected)
                {
                    return entered;
                }

                (context.Stage, context.Draft) = (stageBefore, draftBefore); // the jump did not work: nothing changes
                return _router.Clarify(null, pending);

            case PathKind.Goto:
                _logger.LogWarning("Pippin path refused: {From} → {To} is not allowed.", context.Stage, decision.Stage);
                return _router.Clarify(null, pending);

            case PathKind.Clarify:
                return _router.Clarify(decision.Clarification, pending);

            case PathKind.GoalTalk:
                return await TalkAboutGoalsAsync(context, cancellationToken);

            default:
                return _router.Route(pending);
        }
    }

    // The goals agent answers (with its tools). The stage does not change, so whatever was in progress is resumed;
    // only a cancellation moves on, to the explicit confirmation, which the code asks.
    private async Task<NodeResult> TalkAboutGoalsAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var answer = await _agent.ReplyAsync(context, cancellationToken);

        if (answer.CancellationRequested is { } goal)
        {
            context.Draft = (context.Draft ?? GoalsDraft.Empty(null)) with
            {
                CancelGoalId = goal.Id,
                ReturnStage = context.Stage,
            };
            return NodeResult.Answer(ConfirmCancelGoalNode.Question(goal.Name), Stage.ConfirmCancelGoal);
        }

        var reply = string.IsNullOrWhiteSpace(answer.Text) ? "Acho que não entendi direito. 🤔" : answer.Text;
        return NodeResult.Answer(WithPendingQuestion(reply, context));
    }

    /// <summary>
    /// Ends the reply with the pending question of the current stage ("Voltando ao que eu tinha perguntado: …").
    /// Not at CollectGoals: between tasks its question is only "quer adicionar mais metas?", which would close every
    /// answer about a goal with the same invitation.
    /// </summary>
    private string WithPendingQuestion(string reply, ConversationContext context)
    {
        var pending = context.Stage == Stage.CollectGoals ? null : _nodes[context.Stage].PendingQuestion(context);
        return pending is null ? reply : $"{reply}\n\n{FallbackRouter.BackToQuestion}{pending}";
    }
}
