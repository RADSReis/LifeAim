namespace LifeAim.Modules.Communication.Infrastructure.DiscordBot;

/// <summary>
/// Discord refuses a message longer than 2000 characters, and Pippin sometimes writes more (the summary of a whole
/// day of the weight-loss journal). A long reply goes out as several messages, cut between lines when possible.
/// </summary>
internal static class DiscordText
{
    public const int MaxLength = 2000;

    public static IReadOnlyList<string> Split(string text, int maxLength = MaxLength)
    {
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > maxLength)
        {
            // The last line break (or, failing that, space) that still fits; a single huge word is cut where it must.
            var cut = rest.LastIndexOf('\n', maxLength - 1);
            if (cut <= 0)
            {
                cut = rest.LastIndexOf(' ', maxLength - 1);
            }

            if (cut <= 0)
            {
                cut = maxLength;
            }

            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
        {
            parts.Add(rest);
        }

        return parts;
    }
}
