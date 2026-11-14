using System.Globalization;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// Capitalizes a person's name the way it is written in Portuguese: "rafael diniz dos santos" →
/// "Rafael Diniz dos Santos". Plain code, not the AI: a fixed rule is cheaper and always the same.
/// </summary>
internal static class PersonName
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    // Connecting words stay lowercase in the middle of a name (Alvarez de Menezes, Ana e Silva, Van der Berg).
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "da", "das", "de", "do", "dos", "e", "di", "du", "del", "della", "la", "le", "van", "von", "der", "den", "y",
    };

    public static string Capitalize(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', words.Select((word, index) => Word(word, isFirst: index == 0)));
    }

    private static string Word(string word, bool isFirst)
    {
        // Mixed case was typed on purpose (McDonald, DiCaprio): keep it as the user wrote it.
        if (!IsAllLower(word) && !IsAllUpper(word))
        {
            return word;
        }

        var lower = word.ToLower(PtBr);
        if (!isFirst && Connectors.Contains(lower))
        {
            return lower;
        }

        // Each part of "ana-maria" and "o'neil" gets its capital: Ana-Maria, O'Neil. "d'ávila" → d'Ávila.
        var parts = lower.Split('-').Select(part => Apostrophes(part, isFirst));
        return string.Join('-', parts);
    }

    private static string Apostrophes(string part, bool isFirstWord)
    {
        var pieces = part.Split('\'');
        for (var i = 0; i < pieces.Length; i++)
        {
            var keepLower = i == 0 && pieces.Length > 1 && pieces[0] == "d" && !isFirstWord;
            pieces[i] = keepLower ? pieces[i] : Upper(pieces[i]);
        }

        return string.Join('\'', pieces);
    }

    private static string Upper(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], PtBr) + text[1..];

    private static bool IsAllLower(string word) => word == word.ToLower(PtBr);

    private static bool IsAllUpper(string word) => word == word.ToUpper(PtBr);
}
