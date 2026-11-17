namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// Takes the messages the current node did not expect. It will become AI-driven once Pippin has tools
/// (list goals, record progress...): the AI will pick the tool that fits. With no tools yet, nothing can
/// fit, so it answers that the feature does not exist (UX item 9) and resumes where the user was,
/// without changing the stage.
/// </summary>
internal sealed class FallbackRouter
{
    /// <summary>
    /// One plain sentence, always the same: what Pippin does. (It used to be one of three jokes drawn at random, which
    /// read as a broken bot the third time in a day.)
    /// </summary>
    public const string OutOfScope =
        "Isso eu ainda não faço. 🙂 Eu cuido das suas metas e do seu diário de alimentação, peso e treino.";

    public const string BackToQuestion = "Mas voltando ao que eu tinha perguntado: ";

    /// <param name="pendingQuestion">
    /// The question of the current stage (INode.PendingQuestion). Not Pippin's last message: that may be a
    /// previous router reply, and repeating it would nest the replies inside each other at every message.
    /// </param>
    /// <summary>The message tried to answer the current question but was unclear: say what is needed and ask again.</summary>
    public NodeResult Clarify(string? clarification, string? pendingQuestion)
    {
        var reply = clarification ?? "Acho que não entendi direito. 🤔";
        return NodeResult.Answer(pendingQuestion is null ? reply : $"{reply}\n\n{pendingQuestion}");
    }

    public NodeResult Route(string? pendingQuestion)
    {
        var reply = OutOfScope;

        if (pendingQuestion is not null)
        {
            reply += "\n\n" + BackToQuestion + pendingQuestion;
        }

        return NodeResult.Answer(reply);
    }
}
