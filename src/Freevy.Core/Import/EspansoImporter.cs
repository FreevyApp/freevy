using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Freevy.Core.Import;

/// <summary>
/// Reads Espanso match files (match/*.yml). Each file becomes a folder under "Espanso". Regex
/// triggers, images and forms have no Freevy equivalent and are skipped; "$|$" becomes the cursor,
/// clipboard and date variables become macros, other variables stay as text and are counted.
/// </summary>
public static partial class EspansoImporter
{
    public static string DefaultMatchDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "espanso", "match");

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <param name="path">The match folder, or a single .yml file.</param>
    public static ImportedLibrary Load(string path)
    {
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? "";
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [path];

        var snippets = new List<Snippet>();
        int skipped = 0, adjusted = 0;
        foreach (var file in files)
        {
            var folder = "Espanso/" + Path.ChangeExtension(Path.GetRelativePath(root, file), null).Replace('\\', '/');
            var (fileSnippets, fileSkipped, fileAdjusted) = Parse(File.ReadAllText(file), folder);
            snippets.AddRange(fileSnippets);
            skipped += fileSkipped;
            adjusted += fileAdjusted;
        }

        return new ImportedLibrary(snippets, skipped, adjusted);
    }

    public static ImportedLibrary Parse(string yaml, string folder)
    {
        var document = Yaml.Deserialize<MatchFile?>(yaml) ?? new MatchFile();
        var globals = document.GlobalVars ?? [];
        var snippets = new List<Snippet>();
        int skipped = 0, adjusted = 0;
        foreach (var match in document.Matches ?? [])
        {
            var triggers = (match.Triggers ?? []).Append(match.Trigger).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).Distinct().ToList();
            var text = match.Replace ?? match.Markdown ?? match.Html;
            if (triggers.Count == 0 || text is null || match.Regex is not null)
            {
                skipped++;
                continue;
            }

            var (expansion, complete) = Convert(text, [.. globals, .. match.Vars ?? []]);
            if (!complete) adjusted++;

            // Espanso expands as soon as the trigger is typed; "word" waits for a word boundary.
            var trigger = match.Word == true ? TriggerFlags.WordStart | TriggerFlags.AfterDelimiter : TriggerFlags.Immediate;
            snippets.Add(ImportDefaults.Snippet(triggers, expansion, match.Label ?? "", folder, trigger,
                match.PropagateCase == true ? CaseMode.Adapt : CaseMode.Exact));
        }

        return new ImportedLibrary(snippets, skipped, adjusted);
    }

    private static (string Text, bool Complete) Convert(string text, IReadOnlyList<Variable> vars)
    {
        var complete = true;
        var output = MacroConversion.EscapeLiteral(text).Replace("$|$", "%(cursor)", StringComparison.Ordinal);
        output = VariablePattern().Replace(output, m =>
        {
            var variable = vars.LastOrDefault(v => v.Name == m.Groups[1].Value);
            switch (variable?.Type)
            {
                case "clipboard":
                    return "%(clipboard)";
                case "date" when variable.Params?.TryGetValue("format", out var format) == true && format is string pattern:
                    var macros = MacroConversion.FromStrftime(pattern, out var known);
                    complete &= known;
                    return macros;
                default:
                    complete = false;
                    return m.Value;
            }
        });

        return (output, complete);
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_\-\.]+)\s*\}\}")]
    private static partial Regex VariablePattern();

    private sealed class MatchFile
    {
        public List<Match>? Matches { get; set; }

        public List<Variable>? GlobalVars { get; set; }
    }

    private sealed class Match
    {
        public string? Trigger { get; set; }

        public List<string>? Triggers { get; set; }

        public string? Regex { get; set; }

        public string? Replace { get; set; }

        public string? Markdown { get; set; }

        public string? Html { get; set; }

        public string? Label { get; set; }

        public bool? Word { get; set; }

        public bool? PropagateCase { get; set; }

        public List<Variable>? Vars { get; set; }
    }

    private sealed class Variable
    {
        public string Name { get; set; } = "";

        public string Type { get; set; } = "";

        public Dictionary<string, object>? Params { get; set; }
    }
}
