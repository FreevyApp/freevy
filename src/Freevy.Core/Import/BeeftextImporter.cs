using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Freevy.Core.Import;

/// <summary>
/// Reads Beeftext's comboList.json (file format v2 and later, with the v1 property names as a
/// fallback). Groups become folders under "Beeftext"; disabled combos are skipped. #{cursor},
/// #{clipboard}, dates, #{combo:x} and #{input:x} become macros; the rest (#{powershell:...},
/// #{envVar:...}, case changes) stays as text and is counted.
/// </summary>
public static partial class BeeftextImporter
{
    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "beeftext", "comboList.json");

    public static ImportedLibrary Load(string file) => Parse(File.ReadAllText(file), CultureInfo.CurrentCulture);

    public static ImportedLibrary Parse(string json, CultureInfo culture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("groups", out var groupList) && groupList.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groupList.EnumerateArray())
            {
                if (Text(group, "uuid") is { } uuid) groups[uuid] = Text(group, "name") ?? "";
            }
        }

        var snippets = new List<Snippet>();
        int skipped = 0, adjusted = 0;
        var comboList = root.TryGetProperty("combos", out var combos) && combos.ValueKind == JsonValueKind.Array ? combos.EnumerateArray().ToList() : [];
        foreach (var combo in comboList)
        {
            var keyword = Text(combo, "keyword") ?? Text(combo, "comboText");
            var text = Text(combo, "snippet") ?? Text(combo, "substitutionText");
            var enabled = !combo.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
            if (string.IsNullOrEmpty(keyword) || text is null || !enabled)
            {
                skipped++;
                continue;
            }

            var (expansion, complete) = Convert(text, culture);
            if (!complete) adjusted++;

            var groupName = Text(combo, "group") is { } id && groups.TryGetValue(id, out var name) && name.Length > 0 ? name : null;
            var description = Text(combo, "name") is { Length: > 0 } title ? title : Text(combo, "description") ?? "";
            // Beeftext expands as soon as the keyword is typed; "loose" matching also inside words.
            var loose = combo.TryGetProperty("matchingMode", out var mode) && mode.ToString().Contains("loose", StringComparison.OrdinalIgnoreCase);
            snippets.Add(ImportDefaults.Snippet([keyword], expansion, description, groupName is null ? "Beeftext" : $"Beeftext/{groupName}",
                loose ? TriggerFlags.Immediate : TriggerFlags.Immediate | TriggerFlags.WordStart));
        }

        return new ImportedLibrary(snippets, skipped, adjusted);
    }

    private static (string Text, bool Complete) Convert(string text, CultureInfo culture)
    {
        var complete = true;
        var dateFormat = culture.DateTimeFormat;
        var output = VariablePattern().Replace(MacroConversion.EscapeLiteral(text), m =>
        {
            var variable = m.Groups[1].Value;
            var colon = variable.IndexOf(':');
            var (name, argument) = colon < 0 ? (variable, "") : (variable[..colon], variable[(colon + 1)..]);
            switch (name)
            {
                case "cursor": return "%(cursor)";
                case "clipboard": return "%(clipboard)";
                case "date": return MacroConversion.FromDatePattern(dateFormat.ShortDatePattern);
                case "time": return MacroConversion.FromDatePattern(dateFormat.ShortTimePattern);
                case "dateTime" when argument.Length > 0: return MacroConversion.FromDatePattern(argument);
                case "dateTime": return MacroConversion.FromDatePattern($"{dateFormat.ShortDatePattern} {dateFormat.ShortTimePattern}");
                case "combo" when argument.Length > 0: return $"%(Abbrev {argument})";
                case "input": return argument.Length > 0 ? $"%(Input {argument})" : "%(Input)";
                default:
                    complete = false;
                    return m.Value;
            }
        });

        return (output, complete);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"#\{([^{}]+)\}")]
    private static partial Regex VariablePattern();
}
