namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// One step of Pippin's workflow: it handles the messages that arrive while the conversation is at its
/// stage. It may be plain code or ask the AI; either way, the code decides the next stage (the edge).
/// </summary>
internal interface INode
{
    Stage Stage { get; }

    Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken);
}

/// <summary>What a node did with the message.</summary>
internal sealed record NodeResult
{
    private NodeResult(bool expected, string? reply, Stage? nextStage)
    {
        Expected = expected;
        Reply = reply;
        NextStage = nextStage;
    }

    /// <summary>False when the message was not what this node waits for: the router takes over.</summary>
    public bool Expected { get; }

    public string? Reply { get; }

    /// <summary>The edge to follow; null keeps the conversation at the same stage.</summary>
    public Stage? NextStage { get; }

    public static NodeResult Answer(string reply, Stage? nextStage = null) => new(true, reply, nextStage);

    public static NodeResult NotExpected { get; } = new(false, null, null);
}
