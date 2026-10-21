using System.Text;
using System.Text.RegularExpressions;

namespace Freevy.Core.Import;

/// <summary>
/// Reads snippets from a CSV file: TextExpander's group export ("abbreviation", "snippet", "label"),
/// Beeftext's CSV export ("Group", "Name", "Keyword", "Snippet") or a spreadsheet. Columns are found by
/// their header names; without a recognised header the first column is the abbreviation and the
/// second the text. Commas, semicolons and tabs are detected; quoted fields may span lines.
/// </summary>
public static partial class CsvImporter
{
    private static readonly string[] AbbreviationNames = ["abbreviation", "abbrev", "keyword", "shortcut", "trigger", "autotext"];
    private static readonly string[] TextNames = ["snippet", "content", "expansion", "text", "replace", "phrase", "snippet content"];
    private static readonly string[] DescriptionNames = ["label", "description", "name", "title"];
    private static readonly string[] FolderNames = ["folder", "group", "category"];

    /// <param name="textExpanderCodes">Convert TextExpander's %| cursor, %clipboard and date codes.</param>
    public static ImportedLibrary Load(string file, string folder, TriggerFlags trigger, bool textExpanderCodes) =>
        Parse(File.ReadAllText(file), folder, trigger, textExpanderCodes);

    public static ImportedLibrary Parse(string csv, string folder, TriggerFlags trigger, bool textExpanderCodes)
    {
        var rows = ReadRows(csv.TrimStart('﻿'));
        if (rows.Count == 0)
        {
            return new ImportedLibrary([], 0, 0);
        }

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Column(string[] names) => header.FindIndex(names.Contains);
        var (abbreviation, text, description, group) = (Column(AbbreviationNames), Column(TextNames), Column(DescriptionNames), Column(FolderNames));
        var hasHeader = abbreviation >= 0 && text >= 0;
        if (!hasHeader)
        {
            (abbreviation, text, description, group) = (0, 1, -1, -1);
        }

        var snippets = new List<Snippet>();
        int skipped = 0, adjusted = 0;
        foreach (var row in rows.Skip(hasHeader ? 1 : 0))
        {
            string Cell(int index) => index >= 0 && index < row.Count ? row[index] : "";
            var key = Cell(abbreviation).Trim();
            if (key.Length == 0 || row.Count <= text)
            {
                if (row.Any(c => c.Length > 0)) skipped++;
                continue;
            }

            var (expansion, complete) = textExpanderCodes ? FromTextExpander(Cell(text)) : (MacroConversion.EscapeLiteral(Cell(text)), true);
            if (!complete) adjusted++;
            var rowFolder = Cell(group).Trim().Trim('/');
            snippets.Add(ImportDefaults.Snippet([key], expansion, Cell(description).Trim(), rowFolder.Length > 0 ? $"{folder}/{rowFolder}" : folder, trigger));
        }

        return new ImportedLibrary(snippets, skipped, adjusted);
    }

    // TextExpander codes: %| cursor, %clipboard, %Y %m %d … dates (strftime letters), %% a percent sign,
    // %filltext:name=…% fill-ins and %snippet:abbr% nested snippets. One pass over the raw text, so the
    // text between codes is escaped once and a converted "%%" cannot turn into a macro by accident.
    private static (string Text, bool Complete) FromTextExpander(string text)
    {
        var complete = true;
        var output = new StringBuilder();
        var last = 0;
        foreach (Match m in TextExpanderCode().Matches(text))
        {
            output.Append(MacroConversion.EscapeLiteral(text[last..m.Index]));
            last = m.Index + m.Length;
            var code = m.Value;
            if (code == "%|") output.Append("%(cursor)");
            else if (code == "%clipboard") output.Append("%(clipboard)");
            else if (code == "%%") output.Append(last < text.Length && text[last] == '(' ? "%%" : "%");
            else if (code.StartsWith("%snippet:", StringComparison.Ordinal)) output.Append($"%(Abbrev {code[9..^1]})");
            else if (code.StartsWith("%fill", StringComparison.Ordinal))
            {
                // %filltext:name=First name:default=Jane% → %(Input First name); other options are dropped.
                var name = FillName().Match(code) is { Success: true } n ? n.Groups[1].Value.Trim() : "";
                output.Append(name.Length > 0 ? $"%(Input {name})" : "%(Input)");
                if (!code.StartsWith("%filltext:", StringComparison.Ordinal)) complete = false;
            }
            else output.Append(MacroConversion.FromStrftime(code, out _));
        }

        output.Append(MacroConversion.EscapeLiteral(text[last..]));
        return (output.ToString(), complete);
    }

    [GeneratedRegex(@"%(?:\||clipboard|fill(?:text|area|popup|part)?:[^%]*%|snippet:[^%]+%|[dejmYyBbAaHIMSp%])")]
    private static partial Regex TextExpanderCode();

    [GeneratedRegex(@"name=([^:%]*)")]
    private static partial Regex FillName();

    /// <summary>RFC 4180 rows; the delimiter is whichever of , ; or tab appears most in the first line.</summary>
    private static List<List<string>> ReadRows(string csv)
    {
        var firstLine = csv.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
