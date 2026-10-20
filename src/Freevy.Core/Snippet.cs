namespace Freevy.Core;

/// <summary>
/// Trigger behaviour, stored by Breevy in the <c>t</c> attribute as bit flags.
/// The bit meanings are inferred from the data (only 3 and 5 occur) and still need
/// black-box confirmation, see the Breevy research notes.
/// </summary>
[Flags]
public enum TriggerFlags
{
    None = 0,
    WordStart = 1,
    Immediate = 2,
    AfterDelimiter = 4,
}

/// <summary>
/// Case handling, stored by Breevy in the <c>c</c> attribute (0 and 2 occur in the data).
/// </summary>
public enum CaseMode
{
    Exact = 0,
    IgnoreCase = 1,
    Adapt = 2,
}

/// <param name="Id">Stable identity used by sync; <see cref="Guid.Empty"/> only in data written before ids existed.</param>
/// <param name="LineBreaks">How this snippet's line breaks are sent; null follows the setting.</param>
/// <param name="Protected">
/// When true the expansion is kept only in <paramref name="Encrypted"/> (sealed with the
/// master password) and <paramref name="Expansion"/> is empty.
/// </param>
public sealed record Snippet(
    IReadOnlyList<string> Abbreviations,
    string Expansion,
    string Description,
    CaseMode Case,
    TriggerFlags Trigger,
    string Folder,
    DateTime? Created,
    DateTime? Modified,
    bool Protected = false,
    string? Encrypted = null,
    Guid Id = default,
    LineBreakMode? LineBreaks = null);
