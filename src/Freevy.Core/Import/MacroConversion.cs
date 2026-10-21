using System.Text;

namespace Freevy.Core.Import;

/// <summary>Turns other apps' placeholders into Freevy's Breevy-style %(...) macros.</summary>
public static class MacroConversion
{
    /// <summary>Text from apps without %(...) macros: a literal "%(" must stay literal in Freevy.</summary>
    public static string EscapeLiteral(string text) => text.Replace("%(", "%%(", StringComparison.Ordinal);

    // strftime / chrono letters (Espanso dates, TextExpander date codes) and their Freevy macros.
    private static readonly Dictionary<char, string> Strftime = new()
    {
        ['d'] = "%(Day)", ['e'] = "%(#Day)", ['m'] = "%(Month)", ['Y'] = "%(Year)", ['y'] = "%(YearShort)",
        ['B'] = "%(MonthName)", ['b'] = "%(MonthNameShort)", ['A'] = "%(WeekdayName)", ['a'] = "%(WeekdayNameShort)",
        ['H'] = "%(Hour24)", ['I'] = "%(Hour)", ['M'] = "%(Minute)", ['S'] = "%(Second)", ['p'] = "%(AMorPM)",
        ['j'] = "%(#DayOfYear)", ['%'] = "%",
    };

    /// <summary>"%d.%m.%Y" → "%(Day).%(Month).%(Year)". <paramref name="complete"/> is false if a code was unknown.</summary>
    public static string FromStrftime(string format, out bool complete)
    {
        complete = true;
        var output = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length)
            {
                if (Strftime.TryGetValue(format[i + 1], out var macro))
                {
                    output.Append(macro);
                    i++;
                    continue;
                }

                complete = false;
            }

            output.Append(format[i] == '%' && i + 1 < format.Length && format[i + 1] == '(' ? "%%" : format[i].ToString());
        }

        return output.ToString();
    }

    // Qt and .NET date pattern tokens, longest first so "MMMM" wins over "MM".
    private static readonly (string Token, string Macro)[] Pattern =
    [
        ("dddd", "%(WeekdayName)"), ("ddd", "%(WeekdayNameShort)"), ("dd", "%(Day)"), ("d", "%(#Day)"),
        ("MMMM", "%(MonthName)"), ("MMM", "%(MonthNameShort)"), ("MM", "%(Month)"), ("M", "%(#Month)"),
        ("yyyy", "%(Year)"), ("yy", "%(YearShort)"),
        ("HH", "%(Hour24)"), ("H", "%(#Hour24)"), ("hh", "%(Hour)"), ("h", "%(#Hour)"),
        ("mm", "%(Minute)"), ("m", "%(#Minute)"), ("ss", "%(Second)"), ("s", "%(#Second)"),
        ("tt", "%(AMorPM)"), ("AP", "%(AMorPM)"), ("ap", "%(AMorPM)"),
    ];

    /// <summary>"dd.MM.yyyy" → "%(Day).%(Month).%(Year)"; text in single quotes stays literal.</summary>
    public static string FromDatePattern(string pattern)
    {
        var output = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            if (pattern[i] == '\'')
            {
                var end = pattern.IndexOf('\'', i + 1);
                end = end < 0 ? pattern.Length : end;
                output.Append(EscapeLiteral(pattern[(i + 1)..end]));
                i = end + 1;
                continue;
            }

            var match = Pattern.FirstOrDefault(p => string.CompareOrdinal(pattern, i, p.Token, 0, p.Token.Length) == 0);
            if (match.Token is not null)
            {
                output.Append(match.Macro);
                i += match.Token.Length;
                continue;
            }

            output.Append(pattern[i] == '%' && i + 1 < pattern.Length && pattern[i + 1] == '(' ? "%%" : pattern[i].ToString());
            i++;
        }

        return output.ToString();
    }
}
