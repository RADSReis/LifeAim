namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// One step of Pippin's workflow: it handles the messages that arrive while the conversation is at its
/// stage. It may be plain code or ask the AI; either way, the code decides the next stage (the edge).
/// </summary>
internal interface INode
{
    Stage Stage { get; }

    /// <summary>
    /// The question the user should be answering while the conversation is at this stage, or null when the
    /// node waits for nothing in particular. The router repeats it to bring the user back on track.
    /// It reads the context because it may depend on the state (the goals waiting for confirmation).
    /// </summary>
    string? PendingQuestion(ConversationContext context);

    Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken);
}

/// <summary>What a node did with the message.</summary>
internal sealed record NodeResult
{
    private NodeResult(bool expected, string? reply, Stage? nextStage, bool isGreeting = false)
    {
        Expected = expected;
        Reply = reply;
        NextStage = nextStage;
        IsGreeting = isGreeting;
    }

    /// <summary>False when the message was not what this node waits for: the router takes over.</summary>
    public bool Expected { get; }

    /// <summary>The message was only a greeting: the workflow greets back and repeats the pending question.</summary>
    public bool IsGreeting { get; }

    public string? Reply { get; }

    /// <summary>The edge to follow; null keeps the conversation at the same stage.</summary>
    public Stage? NextStage { get; }

    public static NodeResult Answer(string reply, Stage? nextStage = null) => new(true, reply, nextStage);

    public static NodeResult NotExpected { get; } = new(false, null, null);

    public static NodeResult Greeting { get; } = new(false, null, null, isGreeting: true);
}
