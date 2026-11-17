using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LifeAim.Modules.Agents.Infrastructure.Ai;

/// <summary>
/// Sits between Pippin and the model, for every AI call:
/// - says how much the model should "think". A reasoning model left on its default spends seconds thinking about
///   "is this a greeting?"; the nodes classify and extract, and the agents follow explicit rules, so both run on a
///   low effort unless the configuration says otherwise (OpenAI:ReasoningEffort / OpenAI:AgentReasoningEffort);
/// - lets the agents (the calls with tools) use another model (OpenAI:AgentModel);
/// - logs how long each call took and how many tokens it used, so slowness and cost can be seen instead of guessed.
/// </summary>
internal sealed class PippinChatClient(IChatClient inner, OpenAIOptions settings, ILogger<PippinChatClient> logger)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var isAgent = options?.Tools is { Count: > 0 };
        options = options?.Clone() ?? new ChatOptions();

        var configured = (isAgent ? settings.AgentReasoningEffort : settings.ReasoningEffort)?.Trim().ToLowerInvariant();
        if (options.Reasoning is null && configured == "minimal")
        {
            // The library's enum has no "minimal" (the least a GPT-5 model accepts), so it goes in OpenAI's own options.
#pragma warning disable OPENAI001 // The OpenAI library still marks the reasoning effort as experimental.
            options.RawRepresentationFactory ??= _ => new OpenAI.Chat.ChatCompletionOptions
            {
                ReasoningEffortLevel = new OpenAI.Chat.ChatReasoningEffortLevel("minimal"),
            };
#pragma warning restore OPENAI001
        }
        else if (options.Reasoning is null && Effort(configured) is { } effort)
        {
            options.Reasoning = new ReasoningOptions { Effort = effort };
        }

        if (isAgent && options.ModelId is null && !string.IsNullOrWhiteSpace(settings.AgentModel))
        {
            options.ModelId = settings.AgentModel;
        }

        var watch = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        logger.LogInformation(
            "AI call ({Kind}): {Milliseconds} ms, {Input} tokens in ({Cached} cached), {Output} out ({Reasoning} reasoning)",
            isAgent ? "agent" : "node",
            watch.ElapsedMilliseconds,
            response.Usage?.InputTokenCount,
            response.Usage?.CachedInputTokenCount,
            response.Usage?.OutputTokenCount,
            response.Usage?.ReasoningTokenCount);
        return response;
    }

    /// <summary>
    /// The configured word as the library's value; null (the model's default) for "default" or anything unknown.
    /// "minimal" is handled apart, see above.
    /// </summary>
    internal static ReasoningEffort? Effort(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "none" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        _ => null,
    };
}
