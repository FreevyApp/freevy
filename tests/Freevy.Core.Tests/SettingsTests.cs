using Freevy.Core;
using Freevy.Core.Import.Breevy;

namespace Freevy.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Delimiters_round_trip_through_escaping()
    {
        var escaped = Settings.EscapeDelimiters(Settings.DefaultDelimiters);

        Assert.DoesNotContain('\t', escaped);
        Assert.DoesNotContain('\n', escaped);
        Assert.Equal(Settings.DefaultDelimiters, Settings.UnescapeDelimiters(escaped));
    }

    [Fact]
    public void Masking_is_off_by_default_and_matches_folder_names_when_set()
    {
        var snippet = BvyParser.Parse(ParserTests.Fixture)[2]; // folder Games/WoW

        Assert.False(new Settings().IsMasked(snippet));
        Assert.True(new Settings { MaskFoldersContaining = "wow" }.IsMasked(snippet));
        Assert.False(new Settings { MaskFoldersContaining = "password" }.IsMasked(snippet));
    }
}
