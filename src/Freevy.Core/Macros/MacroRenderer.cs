using System.Globalization;
using System.Text;

namespace Freevy.Core.Macros;

/// <summary>What the app does to type an expansion, in order.</summary>
public abstract record OutputStep;

public sealed record TypeText(string Text) : OutputStep;

public sealed record PressKeys(IReadOnlyList<string> Keys, KeyAction Action) : OutputStep;

public sealed record Wait(TimeSpan Duration) : OutputStep;

public sealed record LaunchTarget(string Target, string Arguments) : OutputStep;

/// <param name="Steps">Text, key presses, waits and launches, in order.</param>
/// <param name="CursorBack">Characters to move left afterwards, for %(cursor).</param>
public sealed record OutputPlan(IReadOnlyList<OutputStep> Steps, int CursorBack)
{
    /// <summary>Everything typed as text, for statistics and previews.</summary>
    public string TypedText => string.Concat(Steps.OfType<TypeText>().Select(t => t.Text));
}

/// <summary>A fill-in the user is asked for before typing. Key is what the answers dictionary uses.</summary>
public sealed record InputRequest(string Key, string Label);

/// <summary>What the renderer needs from the outside world. The defaults make it safe to call anywhere.</summary>
public sealed class MacroContext
{
    public DateTime Now { get; init; } = DateTime.Now;

    public CultureInfo Culture { get; init; } = CultureInfo.CurrentCulture;

    public Func<string?> Clipboard { get; init; } = () => null;

    /// <summary>The expansion of another (unprotected) snippet by abbreviation, or null.</summary>
    public Func<string, string?> FindAbbreviation { get; init; } = _ => null;

    /// <summary>Answers for %(Input)/%(Fill), keyed like InputRequest.Key.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();
}

/// <summary>Turns an expansion with Breevy-compatible macros into the steps that type it.</summary>
public static class MacroRenderer
{
    private const int MaxNesting = 5;

    /// <summary>The fill-ins to ask for, in order of appearance; inputs that share a name are asked once.</summary>
    public static IReadOnlyList<InputRequest> InputsNeeded(string text, MacroContext context)
    {
        var requests = new List<InputRequest>();
        var unnamed = 0;
        foreach (var input in Flatten(text, context, 0, []).OfType<InputToken>())
        {
            if (input.Name is null)
            {
                unnamed++;
                requests.Add(new InputRequest($"#{unnamed}", unnamed == 1 ? "Text" : $"Text {unnamed}"));
            }
            else if (!requests.Any(r => r.Key.Equals(input.Name, StringComparison.OrdinalIgnoreCase)))
            {
                requests.Add(new InputRequest(input.Name, input.Name));
            }
        }

        return requests;
    }

    public static OutputPlan Render(string text, MacroContext context)
    {
        var steps = new List<OutputStep>();
        var pending = new StringBuilder();
        var time = context.Now;
        var unnamed = 0;
        int? cursorAt = null; // text elements typed before %(cursor)
        var typed = 0;

        void Text(string value)
        {
            pending.Append(value);
            typed += new StringInfo(value).LengthInTextElements;
        }

        void Flush()
        {
            if (pending.Length == 0) return;
            steps.Add(new TypeText(pending.ToString()));
            pending.Clear();
        }

        foreach (var token in Flatten(text, context, 0, []))
        {
            switch (token)
            {
                case TextToken t:
                    Text(t.Text);
                    break;
                case DateToken d:
                    Text(FormatDate(d.Name, time, context.Culture));
                    break;
                case DateMathToken m:
                    time = Shift(time, m);
                    break;
                case ClipboardToken:
                    Text(Normalize(context.Clipboard() ?? ""));
                    break;
                case InputToken input:
                    var key = input.Name ?? $"#{++unnamed}";
                    var answer = context.Inputs.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";
                    Text(Normalize(answer));
                    break;
                case CursorToken:
                    cursorAt ??= typed;
                    break;
                case KeyToken k:
                    Flush();
                    steps.Add(new PressKeys(k.Keys, k.Action));
                    break;
                case DelayToken w:
                    Flush();
                    steps.Add(new Wait(w.Duration));
                    break;
                case LaunchToken l:
                    Flush();
                    steps.Add(new LaunchTarget(l.Target, l.Arguments));
                    break;
            }
        }

        Flush();
        return new OutputPlan(steps, cursorAt is null ? 0 : typed - cursorAt.Value);
    }

    /// <summary>Tokens with %(Abbrev) replaced by the other snippet's tokens (cycles and deep nesting stop as text).</summary>
    private static IEnumerable<MacroToken> Flatten(string text, MacroContext context, int depth, HashSet<string> chain)
    {
        foreach (var token in MacroParser.Parse(text))
        {
            if (token is not AbbrevToken nested)
            {
                yield return token;
                continue;
            }

            var inner = depth < MaxNesting && !chain.Contains(nested.Name) ? context.FindAbbreviation(nested.Name) : null;
            if (inner is null)
            {
                yield return new TextToken($"%(Abbrev {nested.Name})");
                continue;
            }

            var next = new HashSet<string>(chain, StringComparer.OrdinalIgnoreCase) { nested.Name };
            foreach (var innerToken in Flatten(inner, context, depth + 1, next))
            {
                yield return innerToken;
            }
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static DateTime Shift(DateTime time, DateMathToken math) => math.Unit switch
    {
        DateUnit.Years => time.AddYears(math.Amount),
        DateUnit.Months => time.AddMonths(math.Amount),
        DateUnit.Days => time.AddDays(math.Amount),
        DateUnit.Hours => time.AddHours(math.Amount),
        DateUnit.Minutes => time.AddMinutes(math.Amount),
        _ => time.AddSeconds(math.Amount),
    };

    public static string FormatDate(string name, DateTime time, CultureInfo culture)
    {
        var invariant = CultureInfo.InvariantCulture;
        return name.ToLowerInvariant() switch
        {
            "day" => time.ToString("dd", invariant),
            "#day" => time.Day.ToString(invariant),
            "month" => time.ToString("MM", invariant),
            "#month" => time.Month.ToString(invariant),
            "monthname" => culture.DateTimeFormat.GetMonthName(time.Month),
            "monthnameshort" => culture.DateTimeFormat.GetAbbreviatedMonthName(time.Month),
            "year" => time.ToString("yyyy", invariant),
            "yearshort" => time.ToString("yy", invariant),
            "weekdayname" => culture.DateTimeFormat.GetDayName(time.DayOfWeek),
            "weekdaynameshort" => culture.DateTimeFormat.GetAbbreviatedDayName(time.DayOfWeek),
            "#dayofyear" => time.DayOfYear.ToString(invariant),
            "#dayofweek" => ((int)time.DayOfWeek + 1).ToString(invariant),
            "#dayofweekmon" => (((int)time.DayOfWeek + 6) % 7 + 1).ToString(invariant),
            "#weekofyear" => invariant.Calendar.GetWeekOfYear(time, CalendarWeekRule.FirstDay, DayOfWeek.Sunday).ToString(invariant),
            "hour" => time.ToString("hh", invariant),
            "#hour" => time.ToString("%h", invariant),
            "hour24" => time.ToString("HH", invariant),
            "#hour24" => time.Hour.ToString(invariant),
            "minute" => time.ToString("mm", invariant),
            "#minute" => time.Minute.ToString(invariant),
            "second" => time.ToString("ss", invariant),
            "#second" => time.Second.ToString(invariant),
            // Always AM/PM, even where the locale writes 24-hour times (as Breevy does).
            "amorpm" => time.Hour < 12 ? "AM" : "PM",
            _ => "",
        };
    }
}
