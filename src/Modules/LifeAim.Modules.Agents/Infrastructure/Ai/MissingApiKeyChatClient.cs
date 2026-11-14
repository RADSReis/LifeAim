using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Infrastructure.Ai;

/// <summary>
/// Stands in for the real IChatClient while OpenAI:ApiKey is missing, so the API and the nodes that need
/// no AI keep working; the workflow turns this exception into a friendly "my brain is off" reply.
/// </summary>
internal sealed class MissingApiKeyChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new AiNotConfiguredException();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new AiNotConfiguredException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

internal sealed class AiNotConfiguredException() : InvalidOperationException("OpenAI:ApiKey is not configured.");
