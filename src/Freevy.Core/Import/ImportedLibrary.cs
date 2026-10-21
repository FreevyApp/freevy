namespace Freevy.Core.Import;

/// <summary>
/// Snippets read from another text expander. <paramref name="Skipped"/> counts entries Freevy cannot
/// use at all (regex triggers, images, forms); <paramref name="Adjusted"/> counts snippets that were
/// imported but contain something only partly converted, such as an unknown variable.
/// </summary>
public sealed record ImportedLibrary(IReadOnlyList<Snippet> Snippets, int Skipped, int Adjusted);

internal static class ImportDefaults
{
    /// <summary>A plain snippet as the other apps mean it: abbreviation, text, and the given trigger.</summary>
    public static Snippet Snippet(IReadOnlyList<string> abbreviations, string expansion, string description, string folder,
        TriggerFlags trigger, CaseMode caseMode = CaseMode.Exact) =>
        new(abbreviations, expansion.Replace("\r\n", "\n"), description, caseMode, trigger, folder, DateTime.Now, DateTime.Now, Id: Guid.NewGuid());
}
