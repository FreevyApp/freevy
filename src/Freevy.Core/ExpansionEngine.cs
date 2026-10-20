using System.Text;

namespace Freevy.Core;

/// <summary>
/// What the output layer must do: delete <see cref="Deleted"/> and type <see cref="Text"/>.
/// <see cref="Suffix"/> is the delimiter that triggered it (already part of both strings).
/// For a protected snippet <see cref="Text"/> holds only the suffix; the caller decrypts the
/// expansion and types it in front of the suffix.
/// </summary>
public sealed record Expansion(string Deleted, string Text, Snippet Snippet, string Suffix);

/// <summary>
/// Platform-independent matching state machine. Feed it the characters the user types;
/// it returns an <see cref="Expansion"/> when an abbreviation fires.
/// </summary>
public sealed class ExpansionEngine
{
    private readonly Dictionary<string, List<(string Abbreviation, Snippet Snippet)>> _index =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<char> _delimiters;
    private readonly int _maxLength;
    private readonly StringBuilder _buffer = new();

    // Whether the character before the buffer's first character is a word boundary.
    private bool _startIsBoundary = true;

    public ExpansionEngine(IEnumerable<Snippet> snippets, string delimiters)
    {
        _delimiters = new HashSet<char>(delimiters);
        foreach (var snippet in snippets)
        {
            foreach (var abbreviation in snippet.Abbreviations)
            {
                if (!_index.TryGetValue(abbreviation, out var list))
                {
                    _index[abbreviation] = list = [];
                }

                list.Add((abbreviation, snippet));
                _maxLength = Math.Max(_maxLength, abbreviation.Length);
            }
        }
    }

    public int AbbreviationCount => _index.Count;

    /// <summary>Forget typed context, e.g. after a click, focus change or caret movement.</summary>
    public void Reset()
    {
        _buffer.Clear();
        _startIsBoundary = true;
    }

    public void Backspace()
    {
        if (_buffer.Length > 0)
        {
            _buffer.Length--;
        }
    }

    public Expansion? Type(char c)
    {
        if (c == '\r')
        {
            c = '\n';
        }

        _buffer.Append(c);
        Trim();

        var buffer = _buffer.ToString();
        var result = Match(buffer, buffer.Length, TriggerFlags.Immediate, suffix: "");
        if (result is null && IsDelimiter(c))
        {
            result = Match(buffer, buffer.Length - 1, TriggerFlags.AfterDelimiter, suffix: c.ToString());
        }

        if (result is not null)
        {
            // The expansion becomes the new context; its last character decides the next word boundary.
            _buffer.Clear();
            _startIsBoundary = result.Text.Length == 0 || IsDelimiter(result.Text[^1]);
        }

        return result;
    }

    private Expansion? Match(string buffer, int end, TriggerFlags requiredFlag, string suffix)
    {
        for (var length = Math.Min(_maxLength, end); length > 0; length--)
        {
            var start = end - length;
            var typed = buffer.Substring(start, length);
            if (!_index.TryGetValue(typed, out var candidates))
            {
                continue;
            }

            foreach (var (abbreviation, snippet) in candidates)
            {
                if ((snippet.Trigger & requiredFlag) == 0)
                {
                    continue;
                }

                if (snippet.Case == CaseMode.Exact && !string.Equals(typed, abbreviation, StringComparison.Ordinal))
                {
                    continue;
                }

                if (snippet.Trigger.HasFlag(TriggerFlags.WordStart) && !IsBoundaryBefore(buffer, start))
                {
                    continue;
                }

                var text = snippet.Protected ? ""
                    : snippet.Case == CaseMode.Adapt ? CaseAdapter.Apply(typed, abbreviation, snippet.Expansion)
                    : snippet.Expansion;
                return new Expansion(typed + suffix, text + suffix, snippet, suffix);
            }
        }

        return null;
    }

    private bool IsBoundaryBefore(string buffer, int index) =>
        index == 0 ? _startIsBoundary : IsDelimiter(buffer[index - 1]);

    private bool IsDelimiter(char c) => _delimiters.Contains(c) || char.IsWhiteSpace(c);

    private void Trim()
    {
        // Keep one extra character so the word-start check can see what precedes the longest abbreviation.
        var keep = _maxLength + 2;
        if (_buffer.Length <= keep)
        {
            return;
        }

        var removeCount = _buffer.Length - keep;
        _startIsBoundary = IsDelimiter(_buffer[removeCount - 1]);
        _buffer.Remove(0, removeCount);
    }
}
