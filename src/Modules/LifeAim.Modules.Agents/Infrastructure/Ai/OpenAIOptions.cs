namespace LifeAim.Modules.Agents.Infrastructure.Ai;

/// <summary>The "OpenAI" configuration section (User Secrets locally, OpenAI__ApiKey on the VPS).</summary>
public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>Without it Pippin still runs, but every AI step answers that its brain is off.</summary>
    public string? ApiKey { get; init; }

    /// <summary>A small, cheap model: each node gives the AI one short, focused task.</summary>
    public string Model { get; init; } = "gpt-5-mini";

    /// <summary>Another model for the agents (the calls with tools); empty uses <see cref="Model"/>.</summary>
    public string? AgentModel { get; init; }

    /// <summary>
    /// How much the model thinks in the nodes' calls (classify, extract): "minimal", "none", "low", "medium", "high",
    /// or "default" to leave it to the model. Low by default: these are short, focused tasks, and every second of
    /// thinking here is a second the user waits before the real answer starts. ("minimal" is faster still, about one
    /// second less per call, but the goal intake was not checked with it.)
    /// </summary>
    public string ReasoningEffort { get; init; } = "low";

    /// <summary>
    /// The same for the agents (goals and fitness). Low, not minimal: measured with the real model, "minimal" made the
    /// fitness agent fill tool arguments wrongly and miscalculate a meal.
    /// </summary>
    public string AgentReasoningEffort { get; init; } = "low";
}
