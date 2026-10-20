namespace Freevy.Core;

/// <summary>
/// "Adapt to case" rule: typing the abbreviation in all caps upper-cases the expansion,
/// capitalising its first letter capitalises the expansion's first letter.
/// </summary>
public static class CaseAdapter
{
    public static string Apply(string typed, string abbreviation, string expansion)
    {
        if (typed == abbreviation || expansion.Length == 0)
        {
            return expansion;
        }

        var letters = typed.Where(char.IsLetter).ToArray();
        if (letters.Length == 0)
        {
            return expansion;
        }

        if (letters.Length > 1 && letters.All(char.IsUpper))
        {
            return expansion.ToUpperInvariant();
        }

        if (char.IsUpper(letters[0]))
        {
            var first = Array.FindIndex(expansion.ToCharArray(), char.IsLetter);
            if (first >= 0)
            {
                return expansion[..first] + char.ToUpperInvariant(expansion[first]) + expansion[(first + 1)..];
            }
        }

        return expansion;
    }
}
