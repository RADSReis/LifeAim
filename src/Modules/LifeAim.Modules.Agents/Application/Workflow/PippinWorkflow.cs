using System.Collections.Concurrent;
using LifeAim.Modules.Agents.Contracts;
using LifeAim.Modules.Agents.Infrastructure.Ai;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.Logging;

namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// Pippin's hand-made workflow engine, run once per incoming message:
/// load the context → run the node of the current stage → if the message was not expected, hand it
/// to the router → follow the edge (next stage) → save the context and both messages → reply.
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
    private readonly IGoalService _goals;
    private readonly ILogger<PippinWorkflow> _logger;
    private readonly Dictionary<Stage, INode> _nodes;

    public PippinWorkflow(
        IConversationStore store,
        IEnumerable<INode> nodes,
        FallbackRouter router,
        GreetingReply greeting,
        PathClassifier classifier,
        IGoalService goals,
        ILogger<PippinWorkflow> logger)
    {
        _store = store;
        _router = router;
        _greeting = greeting;
        _classifier = classifier;
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

            // A greeting asks for nothing: greet back. Anything else unexpected → the path classifier decides.
            return result.IsGreeting
                ? NodeResult.Answer(_greeting.Reply(context, node.PendingQuestion(context)))
                : await FindPathAsync(context, node, cancellationToken);
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

            default:
                return _router.Route(pending);
        }
    }
}
