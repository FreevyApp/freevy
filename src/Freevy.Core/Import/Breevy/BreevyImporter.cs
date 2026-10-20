namespace Freevy.Core.Import.Breevy;

public sealed record BreevyImport(IReadOnlyList<Snippet> Snippets, Settings Settings);

/// <summary>One-way import from a Breevy data folder (abbreviations.bvy and prefs.lt).</summary>
public static class BreevyImporter
{
    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Breevy");

    public static string SnippetsPath(string directory) => Path.Combine(directory, "abbreviations.bvy");

    public static bool IsAvailable(string directory) => File.Exists(SnippetsPath(directory));

    public static BreevyImport Load(string directory)
    {
        var prefs = BreevyPrefs.LoadFile(Path.Combine(directory, "prefs.lt"));
        return new BreevyImport(
            BvyParser.ParseFile(SnippetsPath(directory)),
            new Settings { Delimiters = prefs.Delimiters, ClipboardThreshold = prefs.ClipboardThreshold });
    }
}
