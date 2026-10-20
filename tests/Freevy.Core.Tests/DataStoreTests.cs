using Freevy.Core;
using Freevy.Core.Import.Breevy;

namespace Freevy.Core.Tests;

public sealed class DataStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FreevyTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Round_trips_snippets_and_settings()
    {
        var store = new DataStore(_directory);
        var snippets = BvyParser.Parse(ParserTests.Fixture);
        var settings = new Settings { Delimiters = " .", ClipboardThreshold = 42 };

        store.SaveSnippets(snippets);
        store.SaveSettings(settings);

        var loaded = store.LoadSnippets();
        Assert.Equal(snippets.Count, loaded.Count);
        Assert.Equal(snippets[0].Abbreviations, loaded[0].Abbreviations);
        Assert.Equal(snippets[0] with { Abbreviations = loaded[0].Abbreviations }, loaded[0]);
        Assert.Equal(settings, store.LoadSettings());
    }

    [Fact]
    public void Missing_files_give_empty_snippets_and_default_settings()
    {
        var store = new DataStore(_directory);

        Assert.False(store.HasSnippets);
        Assert.Empty(store.LoadSnippets());
        Assert.Equal(new Settings(), store.LoadSettings());
        Assert.Null(store.BackupSnippets());
    }

    [Fact]
    public void Backup_copies_existing_snippets()
    {
        var store = new DataStore(_directory);
        store.SaveSnippets(BvyParser.Parse(ParserTests.Fixture));

        var backup = store.BackupSnippets();

        Assert.NotNull(backup);
        Assert.Equal(File.ReadAllText(store.SnippetsPath), File.ReadAllText(backup));
    }

    [Fact]
    public void Snippets_without_ids_get_stable_ids_on_first_load()
    {
        var store = new DataStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.SnippetsPath, """
            [{ "Abbreviations": ["teh"], "Expansion": "the", "Description": "", "Case": "Adapt", "Trigger": "WordStart, AfterDelimiter", "Folder": "", "Created": null, "Modified": null }]
            """);

        var first = store.LoadSnippets();
        var second = store.LoadSnippets();

        Assert.NotEqual(Guid.Empty, first[0].Id);
        Assert.Equal(first[0].Id, second[0].Id);
    }

    [Fact]
    public void Breevy_import_gives_every_snippet_its_own_id()
    {
        var snippets = BvyParser.Parse(ParserTests.Fixture);

        Assert.DoesNotContain(snippets, s => s.Id == Guid.Empty);
        Assert.Equal(snippets.Count, snippets.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Sync_state_round_trips_and_can_be_reset()
    {
        var store = new DataStore(_directory);
        var id = Guid.NewGuid();
        var state = new Freevy.Core.Sync.SyncState { Cursor = 42, Items = new() { [id] = new(7, "ABC"), [Guid.NewGuid()] = new(9, null) } };

        store.SaveSyncState(state);
        var loaded = store.LoadSyncState();

        Assert.Equal(42, loaded.Cursor);
        Assert.Equal(new Freevy.Core.Sync.SyncedEntry(7, "ABC"), loaded.Items[id]);
        Assert.Contains(loaded.Items.Values, e => e.Hash is null);

        store.DeleteSyncState();
        Assert.Equal(0, store.LoadSyncState().Cursor);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
