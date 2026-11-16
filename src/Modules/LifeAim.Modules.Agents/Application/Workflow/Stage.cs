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

    /// <summary>
    /// The plan proposal prepared in the background is ready (Funcionalidade 2): Pippin sent, on his own, "Quer dar
    /// uma olhada?" and waits for yes or not now.
    /// </summary>
    OfferPlan,

    /// <summary>Pippin listed up to 3 goals of the proposal and waits for the user to pick one (or another).</summary>
    ChoosePlanGoal,

    /// <summary>
    /// One goal of the proposal in focus: Pippin showed his suggestion and a contextual question, and refines it with
    /// the user until they agree (saved as the goal's plan), skip it, or move on.
    /// </summary>
    RefinePlanGoal,

    /// <summary>
    /// The daily follow-up (Funcionalidade 3): Pippin asked, on his own, about the activities planned for now (and the
    /// ones still unconfirmed) and waits to hear whether they happened. The activities asked are in the draft.
    /// </summary>
    CheckIn,

    /// <summary>
    /// The monthly summary was sent (Funcionalidade 4) and ended with an invitation ("Tive um insight sobre seu hábito
    /// de leitura. Quer saber qual?"): Pippin waits for yes or no. Anything else drops the invitation, without insisting.
    /// </summary>
    SummaryInsight,

    /// <summary>
    /// Priorities and conflicts between goals (Funcionalidade 5): the history showed a recurring difficulty, Pippin
    /// recognized what is working and asked, on his own, what is getting in the way of one goal. He waits for the
    /// answer, may propose a small, gradual adjustment of that goal only (the draft's Plan), and waits for yes or no.
    /// </summary>
    BalanceReview,
}
