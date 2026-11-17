using System.Globalization;
using System.Text;

namespace LifeAim.Modules.Fitness.Domain;

/// <summary>
/// How a food is looked up in the reference table: by its words, the way the table's search_name was written (lower
/// case, no accents, no punctuation). "Ovos cozidos" finds "Ovo, de galinha, inteiro, cozido": plurals are cut.
/// </summary>
internal static class FoodSearch
{
    public const int MaxWords = 6;

    private static readonly HashSet<string> Ignored = ["de", "da", "do", "das", "dos", "com", "sem", "e", "em", "a", "o", "g", "kg", "ml", "un"];

    /// <summary>The words to look for, at most <see cref="MaxWords"/>; empty when the query says nothing.</summary>
    public static IReadOnlyList<string> Words(string? query) =>
        Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 1 && !Ignored.Contains(word) && !word.All(char.IsDigit))
            .Select(Singular)
            .Distinct()
            .Take(MaxWords)
            .ToList();

    public static string Normalize(string? text)
    {
        var result = new StringBuilder();
        foreach (var c in (text ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            result.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : ' ');
        }

        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // "ovos" → "ovo", "cozidos" → "cozido". The search looks for the word inside the name, so the singular finds both.
    // Short words keep their "s" ("pães" → "paes" would not find "pao" either way).
    private static string Singular(string word) => word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;
}
