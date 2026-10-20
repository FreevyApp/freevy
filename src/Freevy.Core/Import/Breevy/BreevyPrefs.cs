using System.Globalization;
using System.Text;

namespace Freevy.Core.Import.Breevy;

/// <summary>
/// Reads Breevy's prefs.lt: a sequence of "key"="value" pairs where values may span
/// lines and use backslash escapes for quotes and backslashes.
/// </summary>
public sealed class BreevyPrefs
{
    public const string DefaultDelimiters = Settings.DefaultDelimiters;
    public const int DefaultClipboardThreshold = 3500;

    private readonly IReadOnlyDictionary<string, string> _values;

    private BreevyPrefs(IReadOnlyDictionary<string, string> values) => _values = values;

    public static BreevyPrefs Empty { get; } = new(new Dictionary<string, string>());

    public string Delimiters => Get("delim_chars") ?? DefaultDelimiters;

    /// <summary>Expansions longer than this are pasted through the clipboard instead of typed.</summary>
    public int ClipboardThreshold =>
        int.TryParse(Get("clipboard_more_than_chars"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : DefaultClipboardThreshold;

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public static BreevyPrefs LoadFile(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;

    public static BreevyPrefs Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (true)
        {
            var key = ReadQuoted(text, ref i);
            if (key is null)
            {
                break;
            }

            while (i < text.Length && text[i] != '=')
            {
                i++;
            }

            i++;
            var value = ReadQuoted(text, ref i);
            if (value is null)
            {
                break;
            }

            values[key] = value;
        }

        return new BreevyPrefs(values);
    }

    private static string? ReadQuoted(string text, ref int i)
    {
        while (i < text.Length && text[i] != '"')
        {
            i++;
        }

        if (i >= text.Length)
        {
            return null;
        }

        i++;
        var builder = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i++];
            if (c == '\\' && i < text.Length)
            {
                builder.Append(text[i++]);
            }
            else if (c == '"')
            {
                return builder.ToString();
            }
            else if (c != '\r')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
