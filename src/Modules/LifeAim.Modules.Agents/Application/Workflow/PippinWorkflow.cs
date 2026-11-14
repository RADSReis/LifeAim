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
    private readonly IGoalService _goals;
    private readonly ILogger<PippinWorkflow> _logger;
    private readonly Dictionary<Stage, INode> _nodes;

    public PippinWorkflow(
        IConversationStore store,
        IEnumerable<INode> nodes,
        FallbackRouter router,
        GreetingReply greeting,
        IGoalService goals,
        ILogger<PippinWorkflow> logger)
    {
        _store = store;
        _router = router;
        _greeting = greeting;
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

            // A greeting asks for nothing: greet back. Anything else unexpected is a request → the router.
            return result.IsGreeting
                ? NodeResult.Answer(_greeting.Reply(context, node.PendingQuestion(context)))
                : _router.Route(node.PendingQuestion(context));
        }
        catch (AiNotConfiguredException)
        {
            _logger.LogWarning("Pippin needs the AI at stage {Stage}, but OpenAI:ApiKey is not configured.", context.Stage);
            return NodeResult.Answer(NoBrainReply);
        }
    }
}
