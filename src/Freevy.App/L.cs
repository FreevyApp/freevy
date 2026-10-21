using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Freevy.App;

/// <summary>
/// Translations for text the app shows outside its pages (tray menu, notifications, errors, window
/// titles). It reads the same ui/i18n/&lt;language&gt;.json files as the pages, keyed by the English text,
/// and falls back to English for anything missing.
/// </summary>
internal static partial class L
{
    private static Dictionary<string, string> _strings = [];

    /// <summary>"en", "tr" or "de"; what the pages and this class translate into.</summary>
    public static string Language { get; private set; } = "en";

    /// <param name="choice">The setting: "" follows the Windows display language.</param>
    public static void Use(string? choice)
    {
        var pick = string.IsNullOrEmpty(choice) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : choice;
        var language = pick is "tr" or "de" ? pick : "en";
        if (language == Language && (_strings.Count > 0 || language == "en"))
        {
            return;
        }

        Language = language;
        _strings = language == "en" ? [] : Load(Path.Combine(AppContext.BaseDirectory, "ui", "i18n", $"{language}.json"));
    }

    public static string T(string text) => _strings.TryGetValue(text, out var translated) ? translated : text;

    /// <summary>T("Signed in as {email}.", ("email", address)): placeholders are filled after translating.</summary>
    public static string T(string text, params (string Key, object? Value)[] values)
    {
        var lookup = values.ToDictionary(v => v.Key, v => Convert.ToString(v.Value, CultureInfo.CurrentCulture) ?? "");
        return Placeholder().Replace(T(text), m => lookup.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing or broken file leaves the app in English rather than failing.
            return [];
        }
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
