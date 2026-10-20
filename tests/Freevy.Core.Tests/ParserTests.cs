using Freevy.Core;
using Freevy.Core.Import.Breevy;

namespace Freevy.Core.Tests;

public class ParserTests
{
    internal const string Fixture = """
        <Breevy n="All Abbreviations / Folders" t="0" d="0" w="0" m="1"><textexpander><dict><key>Expander Mode</key><integer>0</integer></dict></textexpander><f n="Typos" t="0" d="0" w="0"><a a="teh|hte" d="the" e="the" c="2" t="5" r="0" x="0a1007ea0a1432" y="0a1007ea0a1432"/><a a="sevic" d="servic" e="servic" c="2" t="3" r="0" x="0c1c07e113232d" y="0c1c07e113232d"><textexpander><dict><key>label</key><string>ignored</string></dict></textexpander></a></f><f n="Games" t="0" d="0" w="0"><f n="WoW" t="0" d="0" w="0"><a a=";sig" d="signature" e="Kind regards &amp; thanks" c="0" t="2" r="0" x="" y=""/></f></f><a a=":shrug" d="" e="¯\_(ツ)_/¯" c="0" t="5" r="0" x="0a1007ea0a1432" y="0a1007ea0a1432"/></Breevy>
        """;

    [Fact]
    public void Reads_abbreviations_folders_and_attributes()
    {
        var snippets = BvyParser.Parse(Fixture);

        Assert.Equal(4, snippets.Count);

        var teh = snippets[0];
        Assert.Equal(["teh", "hte"], teh.Abbreviations);
        Assert.Equal("the", teh.Expansion);
        Assert.Equal(CaseMode.Adapt, teh.Case);
        Assert.Equal(TriggerFlags.WordStart | TriggerFlags.AfterDelimiter, teh.Trigger);
        Assert.Equal("Typos", teh.Folder);

        Assert.Equal("Games/WoW", snippets[2].Folder);
        Assert.Equal("Kind regards & thanks", snippets[2].Expansion);
        Assert.Equal("", snippets[3].Folder);
        Assert.Equal("¯\\_(ツ)_/¯", snippets[3].Expansion);
    }

    [Fact]
    public void Decodes_breevy_timestamps()
    {
        Assert.Equal(new DateTime(2026, 10, 16, 10, 20, 50), BvyParser.ParseDate("0a1007ea0a1432"));
        Assert.Equal(new DateTime(2017, 12, 28, 19, 35, 45), BvyParser.ParseDate("0c1c07e113232d"));
        Assert.Null(BvyParser.ParseDate(""));
        Assert.Null(BvyParser.ParseDate("zz1007ea0a1432"));
    }

    [Fact]
    public void Parses_prefs_with_escapes_and_multiline_values()
    {
        var prefs = BreevyPrefs.Parse("\"delim_chars\"=\",.\\\"\\\\ \t\n\"\r\n\"clipboard_more_than_chars\"=\"120\"\r\n");

        Assert.Equal(",.\"\\ \t\n", prefs.Delimiters);
        Assert.Equal(120, prefs.ClipboardThreshold);
    }

    [Fact]
    public void Missing_prefs_fall_back_to_breevy_defaults()
    {
        Assert.Equal(BreevyPrefs.DefaultDelimiters, BreevyPrefs.Empty.Delimiters);
        Assert.Equal(3500, BreevyPrefs.Empty.ClipboardThreshold);
    }
}
