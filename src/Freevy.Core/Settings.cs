using System.Text;
using Freevy.Core.Backup;
using Freevy.Core.Sync;

namespace Freevy.Core;

public enum LineBreakMode
{
    /// <summary>Paste expansions that contain a line break through the clipboard.</summary>
    Paste = 0,

    /// <summary>Type line breaks as Shift+Enter, a new line without sending in most chat apps.</summary>
    ShiftEnter = 1,

    /// <summary>Type line breaks as Enter.</summary>
    Enter = 2,
}

public sealed record Settings
{
    public const string DefaultDelimiters = ",.?!()<>[]{}:;\"/\\-' \t\n";

    /// <summary>Whether typed abbreviations are expanded at all.</summary>
    public bool ExpansionEnabled { get; init; } = true;

    /// <summary>Characters that end a word and fire after-delimiter abbreviations.</summary>
    public string Delimiters { get; init; } = DefaultDelimiters;

    /// <summary>Expansions longer than this are pasted through the clipboard instead of typed.</summary>
    public int ClipboardThreshold { get; init; } = 3500;

    /// <summary>
    /// How line breaks inside an expansion reach the app. Typing Enter sends the message in chat
    /// apps (Slack, Discord, Teams), so multi-line expansions are pasted by default.
    /// </summary>
    public LineBreakMode LineBreaks { get; init; } = LineBreakMode.Paste;

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

    /// <summary>Check freevy.app for new versions and install them in the background.</summary>
    public bool AutoUpdate { get; init; } = true;

    /// <summary>
    /// With <see cref="AutoUpdate"/> on: download new versions and install them on exit. Off: only
    /// tell the user a new version exists and install it when they choose to.
    /// </summary>
    public bool AutoInstallUpdates { get; init; } = true;

    /// <summary>Hours between automatic update checks (1, 6, 24 or 168 in the UI).</summary>
    public int UpdateCheckHours { get; init; } = 6;

    /// <summary>Name of the UI theme (see ui/css/themes.css in the app).</summary>
    public string Theme { get; init; } = "freevy";

    /// <summary>The one-time "star us on GitHub" request was answered, either way.</summary>
    public bool StarPromptDone { get; init; }

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
