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

    /// <summary>
    /// How the node starts when the path classifier sends the conversation here in the middle of another stage.
    /// By default it handles the same message, as if it had arrived at this stage ("quero adicionar outra meta:
    /// correr 5 km" → CollectGoals extracts it). <paramref name="goalId"/> is the saved goal the user meant, if any.
    /// </summary>
    Task<NodeResult> EnterAsync(ConversationContext context, Guid? goalId, CancellationToken cancellationToken) =>
        RunAsync(context, cancellationToken);
}

/// <summary>What a node did with the message.</summary>
internal sealed record NodeResult
{
    private NodeResult(
        bool expected, string? reply, Stage? nextStage, bool isGreeting = false, string? note = null, bool resumes = false)
    {
        Expected = expected;
        Reply = reply;
        NextStage = nextStage;
        IsGreeting = isGreeting;
        Note = note;
        Resumes = resumes;
    }

    /// <summary>
    /// The conversation goes back to <see cref="NextStage"/>, where something was in progress: the workflow ends
    /// the reply with that stage's pending question ("Voltando ao que eu tinha perguntado: …").
    /// </summary>
    public bool Resumes { get; }

    /// <summary>
    /// With <see cref="Expected"/> false: something the node already did before finding out the message was not
    /// for it ("Salvei o plano! ✅"). The workflow puts it at the start of whatever reply handles the message.
    /// </summary>
    public string? Note { get; }

    /// <summary>False when the message was not what this node waits for: the router takes over.</summary>
    public bool Expected { get; }

    /// <summary>The message was only a greeting: the workflow greets back and repeats the pending question.</summary>
    public bool IsGreeting { get; }

    public string? Reply { get; }

    /// <summary>The edge to follow; null keeps the conversation at the same stage.</summary>
    public Stage? NextStage { get; }

    public static NodeResult Answer(string reply, Stage? nextStage = null) => new(true, reply, nextStage);

    /// <summary>Answers and goes back to <paramref name="stage"/>, repeating its pending question.</summary>
    public static NodeResult Resume(string reply, Stage stage) => new(true, reply, stage, resumes: true);

    public static NodeResult NotExpected { get; } = new(false, null, null);

    public static NodeResult Greeting { get; } = new(false, null, null, isGreeting: true);

    /// <summary>
    /// The node acted (e.g. took an ignored confirmation as a "sim" and saved) and moved the conversation to
    /// <paramref name="movedTo"/>, but the message itself is about something else: the workflow handles it from
    /// there (greeting or path classifier) and starts the reply with <paramref name="note"/>.
    /// </summary>
    public static NodeResult NotExpectedAfter(string note, Stage? movedTo) => new(false, null, movedTo, note: note);
}
