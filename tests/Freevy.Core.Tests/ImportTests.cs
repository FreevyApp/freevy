using System.Globalization;
using Freevy.Core;
using Freevy.Core.Import;

namespace Freevy.Core.Tests;

public class ImportTests
{
    [Fact]
    public void Espanso_matches_become_snippets_with_their_options()
    {
        const string yaml = """
            matches:
              - trigger: ":sig"
                replace: "Regards,\nOzan$|$"
                label: Signature
              - triggers: [":hi", ":hello"]
                replace: "Hello"
                word: true
                propagate_case: true
              - trigger: ":now"
                replace: "{{today}} at {{clip}}"
                vars:
                  - name: today
                    type: date
                    params:
                      format: "%d.%m.%Y"
                  - name: clip
                    type: clipboard
              - regex: ":(?P<n>\\d+)x"
                replace: "{{n}}"
              - trigger: ":cat"
                image_path: "C:/cat.png"
            """;

        var library = EspansoImporter.Parse(yaml, "Espanso/base");

        Assert.Equal(3, library.Snippets.Count);
        Assert.Equal(2, library.Skipped);
        Assert.Equal(0, library.Adjusted);

        var sig = library.Snippets[0];
        Assert.Equal([":sig"], sig.Abbreviations);
        Assert.Equal("Regards,\nOzan%(cursor)", sig.Expansion);
        Assert.Equal("Signature", sig.Description);
        Assert.Equal("Espanso/base", sig.Folder);
        Assert.Equal(TriggerFlags.Immediate, sig.Trigger);

        var hello = library.Snippets[1];
        Assert.Equal([":hi", ":hello"], hello.Abbreviations);
        Assert.Equal(TriggerFlags.WordStart | TriggerFlags.AfterDelimiter, hello.Trigger);
        Assert.Equal(CaseMode.Adapt, hello.Case);

        Assert.Equal("%(Day).%(Month).%(Year) at %(clipboard)", library.Snippets[2].Expansion);
    }

    [Fact]
    public void Espanso_variables_without_an_equivalent_stay_and_are_counted()
    {
        const string yaml = """
            matches:
              - trigger: ":ip"
                replace: "IP {{ip}} and 100%(sure)"
                vars:
                  - name: ip
                    type: shell
                    params:
                      cmd: "curl ifconfig.me"
            """;

        var library = EspansoImporter.Parse(yaml, "Espanso/base");

        Assert.Equal(1, library.Adjusted);
        // "%(" from another app is literal text in Freevy, so it is escaped.
        Assert.Equal("IP {{ip}} and 100%%(sure)", library.Snippets[0].Expansion);
    }

    [Fact]
    public void Beeftext_combos_keep_groups_and_convert_variables()
    {
        const string json = """
            {
              "fileFormatVersion": 10,
              "groups": [ { "uuid": "{g1}", "name": "Mail" } ],
              "combos": [
                { "uuid": "{c1}", "name": "Signature", "keyword": "sig;", "snippet": "Regards,\n#{cursor}#{clipboard}", "group": "{g1}", "enabled": true },
                { "uuid": "{c2}", "name": "", "keyword": "today;", "snippet": "#{dateTime:dd.MM.yyyy} #{combo:sig;} #{input:Name}", "enabled": true },
                { "uuid": "{c3}", "name": "Old", "keyword": "old;", "snippet": "x", "enabled": false },
                { "uuid": "{c4}", "name": "Env", "keyword": "user;", "snippet": "#{envVar:USERNAME}", "matchingMode": "loose" },
                { "uuid": "{c5}", "comboText": "v1;", "substitutionText": "From version 1" }
              ]
            }
            """;

        var library = BeeftextImporter.Parse(json, CultureInfo.InvariantCulture);

        Assert.Equal(4, library.Snippets.Count);
        Assert.Equal(1, library.Skipped);
        Assert.Equal(1, library.Adjusted);

        var sig = library.Snippets[0];
        Assert.Equal("Regards,\n%(cursor)%(clipboard)", sig.Expansion);
        Assert.Equal("Beeftext/Mail", sig.Folder);
        Assert.Equal("Signature", sig.Description);
        Assert.Equal(TriggerFlags.Immediate | TriggerFlags.WordStart, sig.Trigger);

        Assert.Equal("%(Day).%(Month).%(Year) %(Abbrev sig;) %(Input Name)", library.Snippets[1].Expansion);
        Assert.Equal("Beeftext", library.Snippets[1].Folder);
        Assert.Equal(TriggerFlags.Immediate, library.Snippets[2].Trigger);
        Assert.Equal("From version 1", library.Snippets[3].Expansion);
    }

    [Fact]
    public void TextExpander_csv_finds_columns_by_header_and_converts_codes()
    {
        const string csv = "abbreviation,snippet,label\r\n"
            + "sig,\"Regards,\r\nOzan%|\",Signature\r\n"
            + "dt,%d.%m.%Y %clipboard,Date\r\n"
            + "pct,\"100%% sure, \"\"quoted\"\" and 5%(x)\",\r\n"
            + "fill,%filltext:name=First name:default=Jane% %snippet:sig%,\r\n"
            + ",orphan text,\r\n";

        var library = CsvImporter.Parse(csv, "TextExpander", TriggerFlags.AfterDelimiter, textExpanderCodes: true);

        Assert.Equal(4, library.Snippets.Count);
        Assert.Equal(1, library.Skipped);
        Assert.Equal("Regards,\nOzan%(cursor)", library.Snippets[0].Expansion);
        Assert.Equal("Signature", library.Snippets[0].Description);
        Assert.Equal("%(Day).%(Month).%(Year) %(clipboard)", library.Snippets[1].Expansion);
        Assert.Equal("100% sure, \"quoted\" and 5%%(x)", library.Snippets[2].Expansion);
        Assert.Equal("%(Input First name) %(Abbrev sig)", library.Snippets[3].Expansion);
        Assert.All(library.Snippets, s => Assert.Equal("TextExpander", s.Folder));
    }

    [Fact]
    public void Plain_csv_without_header_uses_the_first_two_columns_and_semicolons()
    {
        const string csv = "brb;be right back\nty;thank you;ignored\n";

        var library = CsvImporter.Parse(csv, "Imported", TriggerFlags.AfterDelimiter, textExpanderCodes: false);

        Assert.Equal(["brb", "ty"], library.Snippets.Select(s => s.Abbreviations[0]));
        Assert.Equal("thank you", library.Snippets[1].Expansion);
    }

    [Fact]
    public void Beeftext_csv_export_puts_groups_in_folders()
    {
        const string csv = "Group,Name,Keyword,Snippet\nMail,Signature,sig;,Regards\n";

        var library = CsvImporter.Parse(csv, "Beeftext", TriggerFlags.Immediate, textExpanderCodes: false);

        var snippet = Assert.Single(library.Snippets);
        Assert.Equal("sig;", snippet.Abbreviations[0]);
        Assert.Equal("Signature", snippet.Description);
        Assert.Equal("Beeftext/Mail", snippet.Folder);
    }

    [Theory]
    [InlineData("%(Day).%(Month).%(Year) %(Abbrev sig;) %(Input Name)")]
    [InlineData("%(Input First name) %(Abbrev sig)")]
    [InlineData("%(WeekdayName) at %(#Hour) %(AMorPM), %(#Minute):%(#Second) %(#Month)/%(#DayOfYear)")]
    [InlineData("Regards,\nOzan%(cursor)%(clipboard)")]
    public void Converted_macros_are_ones_freevy_understands(string expansion)
    {
        // Anything the parser does not recognise would stay as visible "%(...)" text.
        var leftovers = Freevy.Core.Macros.MacroParser.Parse(expansion)
            .OfType<Freevy.Core.Macros.TextToken>()
            .Where(t => t.Text.Contains("%(", StringComparison.Ordinal));
        Assert.Empty(leftovers);
    }

    [Theory]
    [InlineData("dd.MM.yyyy", "%(Day).%(Month).%(Year)")]
    [InlineData("d MMMM yyyy, HH:mm", "%(#Day) %(MonthName) %(Year), %(Hour24):%(Minute)")]
    [InlineData("dddd 'at' h ap", "%(WeekdayName) at %(#Hour) %(AMorPM)")]
    public void Date_patterns_become_date_macros(string pattern, string expected)
    {
        Assert.Equal(expected, MacroConversion.FromDatePattern(pattern));
    }
}
