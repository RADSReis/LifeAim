namespace LifeAim.Modules.Agents.Contracts;

/// <summary>
/// Pippin, as the channels see it: a message from a LifeAim user goes in, Pippin's reply comes out.
/// Channels (Discord today, maybe Telegram later) only talk to this, so Pippin knows nothing about them.
/// </summary>
public interface IPippin
{
    Task<string> ReplyAsync(PippinMessage message, CancellationToken cancellationToken = default);
}

/// <param name="UserId">LifeAim's user id (not the channel's).</param>
/// <param name="DisplayName">How the channel calls the user, until Pippin learns their name.</param>
/// <param name="Text">What the user wrote (may be empty when they only sent a photo).</param>
/// <param name="Images">Photos sent with the message (a plate, a nutrition label, the scale), if any.</param>
public sealed record PippinMessage(Guid UserId, string DisplayName, string Text, IReadOnlyList<PippinImage>? Images = null);

/// <summary>
/// A photo as it arrived. It is shown to the AI for this one message and then dropped: Pippin keeps what was read
/// from it (the meal, the label's numbers), never the image.
/// </summary>
/// <param name="MediaType">"image/jpeg", "image/png", "image/webp".</param>
public sealed record PippinImage(byte[] Data, string MediaType)
{
    /// <summary>The most photos one message may carry; the rest is ignored.</summary>
    public const int MaxPerMessage = 3;

    /// <summary>The largest photo accepted, in bytes (5 MB).</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    public static bool IsSupported(string? mediaType) =>
        mediaType?.Split(';')[0].Trim().ToLowerInvariant() is "image/jpeg" or "image/png" or "image/webp";
}
