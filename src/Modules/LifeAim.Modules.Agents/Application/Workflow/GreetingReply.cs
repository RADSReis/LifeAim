using System.Text;

namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// The answer to a message that is only a greeting ("oi", "olá", "bom dia"), at any stage (UX item 11,
/// "Saudação em conversas recorrentes"). A greeting asks for nothing, so it is not a request for the router:
/// Pippin greets back, lists the user's goals (at most 10) and brings them back to where they stopped.
/// Plain code: the AI only said "this is a greeting".
/// </summary>
internal sealed class GreetingReply
{
    /// <summary>UX item 11: show at most 10 goals; with more, the first 10 and "...".</summary>
    public const int MaxListedGoals = 10;

    public string Reply(ConversationContext context, string? pendingQuestion)
    {
        var text = new StringBuilder(context.UserName is null ? "Oi! 😊" : $"Opa! Olá, {context.UserName}! 😊");

        // Goals waiting for confirmation come first: the greeting resumes them instead of listing the saved ones.
        var listsGoals = context.Stage != Stage.ConfirmGoals && context.SavedGoals.Count > 0;
        if (listsGoals)
        {
            text.Append("\n\n**Suas metas:**");
            foreach (var goal in context.SavedGoals.Take(MaxListedGoals))
            {
                text.Append("\n- ").Append(goal.Name);
            }

            if (context.SavedGoals.Count > MaxListedGoals)
            {
                text.Append("\n- ...");
            }
        }

        // Until Pippin can talk about a saved goal (next step), it ends with what it can do now: the pending question.
        if (pendingQuestion is null)
        {
            return text.ToString();
        }

        if (listsGoals)
        {
            text.Append("\n\n").Append(pendingQuestion);
        }
        else if (pendingQuestion.Contains('\n'))
        {
            text.Append(" Voltando de onde paramos:\n\n").Append(pendingQuestion);
        }
        else
        {
            text.Append(' ').Append(pendingQuestion);
        }

        return text.ToString();
    }
}
