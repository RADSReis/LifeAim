namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// Takes the messages the current node did not expect. It will become AI-driven once Pippin has tools
/// (list goals, record progress...): the AI will pick the tool that fits. With no tools yet, nothing can
/// fit, so it answers that the feature does not exist (UX item 9) and resumes where the user was,
/// without changing the stage.
/// </summary>
internal sealed class FallbackRouter
{
    private static readonly string[] NoFeatureReplies =
    [
        "Hmm, procurei aqui nos meus bolsos e não achei essa funcionalidade. 🕵️",
        "Isso aí ainda não está no meu repertório. Juro que não é preguiça! 😅",
        "Vasculhei meu manual de instruções e essa página não existe… 📖❌",
    ];

    public const string BackToQuestion = "Mas voltando ao que eu tinha perguntado: ";

    /// <param name="pendingQuestion">
    /// The question of the current stage (INode.PendingQuestion). Not Pippin's last message: that may be a
    /// previous router reply, and repeating it would nest the replies inside each other at every message.
    /// </param>
    public NodeResult Route(string? pendingQuestion)
    {
        var reply = NoFeatureReplies[Random.Shared.Next(NoFeatureReplies.Length)];

        if (pendingQuestion is not null)
        {
            reply += "\n\n" + BackToQuestion + pendingQuestion;
        }

        return NodeResult.Answer(reply);
    }
}
