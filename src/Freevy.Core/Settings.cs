using System.Text;
using Freevy.Core.Backup;
using Freevy.Core.Sync;

namespace Freevy.Core;

public sealed record Settings
{
    public const string DefaultDelimiters = ",.?!()<>[]{}:;\"/\\-' \t\n";

    /// <summary>Whether typed abbreviations are expanded at all.</summary>
    public bool ExpansionEnabled { get; init; } = true;

    /// <summary>Characters that end a word and fire after-delimiter abbreviations.</summary>
    public string Delimiters { get; init; } = DefaultDelimiters;

    /// <summary>Expansions longer than this are pasted through the clipboard instead of typed.</summary>
    public int ClipboardThreshold { get; init; } = 3500;

    /// <summary>Trigger used for snippets created in the editor.</summary>
    public TriggerFlags DefaultTrigger { get; init; } = TriggerFlags.WordStart | TriggerFlags.AfterDelimiter;

    /// <summary>Case mode used for snippets created in the editor.</summary>
    public CaseMode DefaultCase { get; init; } = CaseMode.Adapt;

    /// <summary>
    /// When not empty, expansions in folders whose name contains this text are hidden in the
    /// snippet list and excluded from search. Empty disables masking.
    /// </summary>
    public string MaskFoldersContaining { get; init; } = "";

    /// <summary>How long protected snippets stay usable after entering the master password.</summary>
    public int UnlockMinutes { get; init; } = 5;

    /// <summary>Forget the master key as soon as Windows is locked.</summary>
    public bool LockWhenWindowsLocks { get; init; } = true;

    public BackupSettings Backup { get; init; } = new();

    public SyncSettings Sync { get; init; } = new();

    /// <summary>Name of the UI theme (see ui/css/themes.css in the app).</summary>
    public string Theme { get; init; } = "freevy";

    public bool IsMasked(Snippet snippet) =>
        MaskFoldersContaining.Length > 0
        && snippet.Folder.Contains(MaskFoldersContaining, StringComparison.OrdinalIgnoreCase);

    /// <summary>Shows control characters as \t, \n and \\ so delimiters can be edited in one line.</summary>
    public static string EscapeDelimiters(string delimiters)
    {
        var builder = new StringBuilder();
        foreach (var c in delimiters)
        {
            builder.Append(c switch
            {
                '\t' => "\\t",
                '\n' => "\\n",
                '\\' => "\\\\",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    public static string UnescapeDelimiters(string text)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                builder.Append(text[i] switch
                {
                    't' => '\t',
                    'n' => '\n',
                    _ => text[i],
                });
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        // Duplicates would be harmless but confusing when the value is shown again.
        return new string(builder.ToString().Distinct().ToArray());
    }
}
