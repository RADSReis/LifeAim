namespace LifeAim.Modules.Agents.Infrastructure.Ai;

/// <summary>The "OpenAI" configuration section (User Secrets locally, OpenAI__ApiKey on the VPS).</summary>
public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>Without it Pippin still runs, but every AI step answers that its brain is off.</summary>
    public string? ApiKey { get; init; }

    /// <summary>A small, cheap model: each node gives the AI one short, focused task.</summary>
    public string Model { get; init; } = "gpt-5-mini";
}
