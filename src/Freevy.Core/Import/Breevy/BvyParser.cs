using System.Globalization;
using System.Xml.Linq;

namespace Freevy.Core.Import.Breevy;

/// <summary>
/// Reads Breevy's abbreviations.bvy file. The authoritative data lives in the
/// attributes of the &lt;f&gt; (folder) and &lt;a&gt; (abbreviation) elements; the nested
/// &lt;textexpander*&gt; plist blocks are TextExpander compatibility copies and are ignored.
/// </summary>
public static class BvyParser
{
    public static IReadOnlyList<Snippet> ParseFile(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<Snippet> Parse(string xml)
    {
        var root = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root
            ?? throw new FormatException("Empty .bvy document.");
        if (root.Name.LocalName != "Breevy")
        {
            throw new FormatException($"Unexpected root element <{root.Name.LocalName}>.");
        }

        var snippets = new List<Snippet>();
        Walk(root, "", snippets);
        return snippets;
    }

    private static void Walk(XElement container, string folder, List<Snippet> output)
    {
        foreach (var element in container.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "f":
                    var name = (string?)element.Attribute("n") ?? "";
                    Walk(element, folder.Length == 0 ? name : folder + "/" + name, output);
                    break;
                case "a":
                    output.Add(ReadAbbreviation(element, folder));
                    break;
            }
        }
    }

    private static Snippet ReadAbbreviation(XElement element, string folder)
    {
        var abbreviations = ((string?)element.Attribute("a") ?? "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries);

        return new Snippet(
            abbreviations,
            (string?)element.Attribute("e") ?? "",
            (string?)element.Attribute("d") ?? "",
            (CaseMode)ReadInt(element, "c"),
            (TriggerFlags)ReadInt(element, "t"),
            folder,
            ParseDate((string?)element.Attribute("x")),
            ParseDate((string?)element.Attribute("y")),
            Id: Guid.NewGuid());
    }

    private static int ReadInt(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>
    /// Breevy timestamps are 7 hex-encoded bytes: month, day, year (2 bytes, big endian),
    /// hour, minute, second. Example: 0a1007ea0a1432 is 2026-10-16 10:20:50.
    /// </summary>
    public static DateTime? ParseDate(string? hex)
    {
        if (hex is null || hex.Length != 14)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromHexString(hex);
            return new DateTime(bytes[2] << 8 | bytes[3], bytes[0], bytes[1], bytes[4], bytes[5], bytes[6]);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
