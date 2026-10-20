using System.Text;
using System.Text.RegularExpressions;

namespace Freevy.Core.Macros;

/// <summary>One piece of a parsed expansion: plain text or a %(…) macro.</summary>
public abstract record MacroToken;

public sealed record TextToken(string Text) : MacroToken;

/// <summary>A date or time part such as %(Day) or %(#Hour24); Name is lowercase and keeps the leading #.</summary>
public sealed record DateToken(string Name) : MacroToken;

/// <summary>%(AddDays 3), %(SubtractMonths 1): shifts the time used by the date macros that follow.</summary>
public sealed record DateMathToken(DateUnit Unit, int Amount) : MacroToken;

public enum DateUnit { Years, Months, Days, Hours, Minutes, Seconds }

public enum KeyAction { Press, Down, Up }

/// <summary>%(Key Shift+Enter), %(KeyDown Ctrl), %(KeyUp Ctrl), and %(B)/%(I)/%(U).</summary>
public sealed record KeyToken(IReadOnlyList<string> Keys, KeyAction Action) : MacroToken;

public sealed record DelayToken(TimeSpan Duration) : MacroToken;

public sealed record CursorToken : MacroToken;

public sealed record ClipboardToken : MacroToken;

/// <summary>%(Input), %(Input name), %(Fill name): asked for before the text is typed. Inputs with the same name share one answer.</summary>
public sealed record InputToken(string? Name) : MacroToken;

/// <summary>%(Launch "C:\path\app.exe" arguments): opens a program, file, folder or website.</summary>
public sealed record LaunchToken(string Target, string Arguments) : MacroToken;

/// <summary>%(Abbrev name) or %(Abbreviation name): the expansion of another snippet.</summary>
public sealed record AbbrevToken(string Name) : MacroToken;

/// <summary>
/// Reads Breevy-compatible macros out of an expansion. Unknown or malformed %(…) stays as typed, so
/// ordinary text that happens to contain "%(" is never lost. "%%(" types a literal "%(".
/// </summary>
public static partial class MacroParser
{
    public static readonly IReadOnlySet<string> DateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "day", "#day", "month", "#month", "monthname", "monthnameshort", "year", "yearshort",
        "weekdayname", "weekdaynameshort", "#dayofyear", "#dayofweek", "#dayofweekmon", "#weekofyear",
        "hour", "#hour", "hour24", "#hour24", "minute", "#minute", "second", "#second", "amorpm",
    };

    public static IReadOnlyList<MacroToken> Parse(string text)
    {
        var tokens = new List<MacroToken>();
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '%' && i + 2 < text.Length && text[i + 1] == '%' && text[i + 2] == '(')
            {
                plain.Append("%(");
                i += 3;
                continue;
            }

            if (text[i] == '%' && i + 1 < text.Length && text[i + 1] == '(')
            {
                var end = FindClose(text, i + 2);
                if (end > 0 && TryParseMacro(text[(i + 2)..end], out var token))
                {
                    Flush(tokens, plain);
                    tokens.Add(token);
                    i = end + 1;
                    // %(Key Shift+Enter) followed by a real line break means one line break: the
                    // macro is the break, the newline after it only lays out the snippet.
                    if (token is KeyToken { Action: KeyAction.Press } key && key.Keys[^1] is "enter" or "return")
                    {
                        if (i < text.Length && text[i] == '\r') i++;
                        if (i < text.Length && text[i] == '\n') i++;
                    }

                    continue;
                }
            }

            plain.Append(text[i]);
            i++;
        }

        Flush(tokens, plain);
        return tokens;
    }

    /// <summary>True when the text contains anything Parse would treat as a macro.</summary>
    public static bool HasMacros(string text) => text.Contains("%(", StringComparison.Ordinal) && Parse(text).Any(t => t is not TextToken);

    private static void Flush(List<MacroToken> tokens, StringBuilder plain)
    {
        if (plain.Length == 0) return;
        tokens.Add(new TextToken(plain.ToString()));
        plain.Clear();
    }

    // The closing parenthesis, skipping any inside "quoted" Launch paths.
    private static int FindClose(string text, int start)
    {
        var quoted = false;
        for (var j = start; j < text.Length; j++)
        {
            if (text[j] == '"') quoted = !quoted;
            else if (text[j] == ')' && !quoted) return j;
            else if (text[j] == '\n') return -1;
        }

        return -1;
    }

    private static bool TryParseMacro(string body, out MacroToken token)
    {
        token = null!;
        var trimmed = body.Trim();
        var space = trimmed.IndexOf(' ');
        var name = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var argument = space < 0 ? "" : trimmed[(space + 1)..].Trim();

        if (argument.Length == 0 && DateNames.Contains(name))
        {
            token = new DateToken(name);
            return true;
        }

        switch (name)
        {
            case "cursor" when argument.Length == 0:
                token = new CursorToken();
                return true;
            case "clipboard" when argument.Length == 0:
                token = new ClipboardToken();
                return true;
            case "input" or "fill":
                token = new InputToken(argument.Length == 0 ? null : argument);
                return true;
            case "abbrev" or "abbreviation" when argument.Length > 0:
                token = new AbbrevToken(argument);
                return true;
            case "b" or "i" or "u" when argument.Length == 0:
                token = new KeyToken(["ctrl", name], KeyAction.Press);
                return true;
            case "key" or "keydown" or "keyup" when argument.Length > 0:
                var keys = argument.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.ToLowerInvariant()).ToList();
                if (keys.Count == 0 || keys.Any(k => !KeyNames.IsKnown(k))) return false;
                token = new KeyToken(keys, name == "key" ? KeyAction.Press : name == "keydown" ? KeyAction.Down : KeyAction.Up);
                return true;
            case "delay" when TryParseDelay(argument, out var delay):
                token = new DelayToken(delay);
                return true;
            case "launch" when argument.Length > 0:
                token = ParseLaunch(argument);
                return true;
        }

        var math = DateMath().Match(trimmed);
        if (math.Success)
        {
            var amount = int.Parse(math.Groups["amount"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var unit = Enum.Parse<DateUnit>(math.Groups["unit"].Value, ignoreCase: true);
            var subtract = !math.Groups["op"].Value.Equals("add", StringComparison.OrdinalIgnoreCase);
            token = new DateMathToken(unit, subtract ? -amount : amount);
            return true;
        }

        return false;
    }

    /// <summary>"1s100ms", "500ms", "2s", or a plain number of milliseconds.</summary>
    private static bool TryParseDelay(string argument, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (argument.Length == 0) return false;
        if (int.TryParse(argument, out var plainMs))
        {
            delay = TimeSpan.FromMilliseconds(plainMs);
            return plainMs >= 0;
        }

        var parts = DelayPart().Matches(argument.Replace(" ", ""));
        if (parts.Count == 0 || string.Concat(parts.Select(p => p.Value)).Length != argument.Replace(" ", "").Length) return false;
        foreach (Match part in parts)
        {
            var value = int.Parse(part.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
            delay += part.Groups["u"].Value.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(value),
                "s" => TimeSpan.FromSeconds(value),
                "m" => TimeSpan.FromMinutes(value),
                _ => TimeSpan.Zero,
            };
        }

        // Never let a typo freeze typing for long.
        return delay <= TimeSpan.FromMinutes(1);
    }

    private static LaunchToken ParseLaunch(string argument)
    {
        if (argument.StartsWith('"'))
        {
            var close = argument.IndexOf('"', 1);
            if (close > 0)
            {
                return new LaunchToken(argument[1..close], argument[(close + 1)..].Trim());
            }
        }

        var space = argument.IndexOf(' ');
        return space < 0 ? new LaunchToken(argument, "") : new LaunchToken(argument[..space], argument[(space + 1)..].Trim());
    }

    [GeneratedRegex(@"^(?<op>add|subtract|sub)(?<unit>years|months|days|hours|minutes|seconds)\s+(?<amount>\d{1,6})$", RegexOptions.IgnoreCase)]
    private static partial Regex DateMath();

    [GeneratedRegex(@"(?<n>\d+)(?<u>ms|s|m)", RegexOptions.IgnoreCase)]
    private static partial Regex DelayPart();
}
