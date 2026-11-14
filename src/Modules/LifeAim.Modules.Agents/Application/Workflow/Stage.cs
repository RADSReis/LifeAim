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
}
