namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// Where a user is in Pippin's workflow: each stage is handled by one node. Saved by name in
/// agents.conversations.stage, so renaming a member needs a migration.
/// </summary>
internal enum Stage
{
    /// <summary>First contact: Pippin introduces itself and asks the user's name.</summary>
    Greeting,

    /// <summary>Waiting for the user's name.</summary>
    AskName,

    /// <summary>Waiting for the user's goals (Funcionalidade 1): the period, the goals, or both at once.</summary>
    CollectGoals,

    /// <summary>Pippin showed the goals it understood and waits for "sim", a correction, or something else.</summary>
    ConfirmGoals,

    /// <summary>
    /// Saved goals that miss what makes them plannable (indicator, target, due date), one at a time: Pippin
    /// proposes the missing parts and waits for "sim", a correction or "pular".
    /// </summary>
    DetailGoals,

    /// <summary>
    /// The user asked to cancel a saved goal (through the goals agent): Pippin asked "Tem certeza?" and waits for an
    /// explicit "sim" (Funcionalidade 1, decision 5). Then the conversation goes back to where it was.
    /// </summary>
    ConfirmCancelGoal,
}
