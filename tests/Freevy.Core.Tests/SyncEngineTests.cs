using System.Security.Cryptography;
using Freevy.Core.Security;
using Freevy.Core.Sync;

namespace Freevy.Core.Tests;

public class SyncEngineTests
{
    private static readonly byte[] ItemKey = AccountCrypto.ItemKey(RandomNumberGenerator.GetBytes(32));

    /// <summary>In-memory stand-in with the same rules as the Laravel SyncService.</summary>
    private sealed class FakeServer : ISyncTransport
    {
        public Dictionary<Guid, RemoteItem> Items { get; } = [];

        public long Revision { get; private set; }

        public int Pushed { get; private set; }

        public Task<PullPage> PullAsync(long since, CancellationToken cancellationToken)
        {
            var page = Items.Values.Where(i => i.Revision > since).OrderBy(i => i.Revision).Take(2).ToList();
            var more = Items.Values.Count(i => i.Revision > since) > page.Count;
            return Task.FromResult(new PullPage(page, page.LastOrDefault()?.Revision ?? since, more));
        }

        public Task<IReadOnlyList<PushResult>> PushAsync(IReadOnlyList<PushChange> changes, CancellationToken cancellationToken)
        {
            var results = new List<PushResult>();
            foreach (var change in changes)
            {
                Items.TryGetValue(change.Id, out var item);
                if (item is null && change.Deleted)
                {
                    results.Add(new PushResult(change.Id, true, null, null));
                    continue;
                }

                if ((item is null && change.BaseRevision is not (null or 0)) || (item is not null && change.BaseRevision != item.Revision))
                {
                    results.Add(new PushResult(change.Id, false, null, item));
                    continue;
                }

                Items[change.Id] = new RemoteItem(change.Id, ++Revision, change.Deleted, change.Deleted ? null : change.Ciphertext);
                Pushed++;
                results.Add(new PushResult(change.Id, true, Revision, null));
            }

            return Task.FromResult<IReadOnlyList<PushResult>>(results);
        }
    }

    /// <summary>One PC: its snippets, vault and sync state.</summary>
    private sealed class Device(FakeServer server, params Snippet[] snippets)
    {
        public List<Snippet> Snippets { get; set; } = [.. snippets];

        public VaultHeader? Vault { get; set; }

        public SyncState State { get; private set; } = new();

        public async Task<SyncOutcome> SyncAsync()
        {
            var outcome = await new SyncEngine(server, ItemKey).RunAsync(Snippets, Vault, State, CancellationToken.None);
            Snippets = [.. outcome.Snippets];
            Vault = outcome.Vault;
            State = outcome.State;
            return outcome;
        }

        public void Edit(string abbreviation, Func<Snippet, Snippet> change)
        {
            var index = Snippets.FindIndex(s => s.Abbreviations.Contains(abbreviation));
            Snippets[index] = change(Snippets[index]);
        }
    }

    private static Snippet NewSnippet(string abbreviation, string expansion, DateTime? modified = null) =>
        new([abbreviation], expansion, "", CaseMode.Adapt, TriggerFlags.WordStart | TriggerFlags.AfterDelimiter, "Folder",
            new DateTime(2026, 1, 1), modified ?? new DateTime(2026, 1, 1), Id: Guid.NewGuid());

    private static string[] Summary(Device device) =>
        device.Snippets.Select(s => $"{string.Join('|', s.Abbreviations)}={s.Expansion}@{s.Folder}").Order().ToArray();

    [Fact]
    public async Task A_new_device_receives_everything_and_a_repeat_sync_is_quiet()
    {
        var server = new FakeServer();
        var first = new Device(server, NewSnippet("teh", "the"), NewSnippet("adn", "and"), NewSnippet(";sig", "Regards"));
        var second = new Device(server);

        var upload = await first.SyncAsync();
        var download = await second.SyncAsync();
        var again = await first.SyncAsync();

        Assert.Equal(3, upload.Uploaded);
        Assert.Equal(3, download.Downloaded);
        Assert.Equal(Summary(first), Summary(second));
        Assert.Equal(first.Snippets.Select(s => s.Id).Order(), second.Snippets.Select(s => s.Id).Order());
        Assert.Equal(0, again.Uploaded);
        Assert.Equal(0, again.Downloaded);
    }

    [Fact]
    public async Task Edits_and_deletes_travel_between_devices()
    {
        var server = new FakeServer();
        var first = new Device(server, NewSnippet("teh", "the"), NewSnippet("adn", "and"));
        var second = new Device(server);
        await first.SyncAsync();
        await second.SyncAsync();

        first.Edit("teh", s => s with { Expansion = "THE", Modified = new DateTime(2026, 2, 1) });
        first.Snippets.RemoveAll(s => s.Abbreviations.Contains("adn"));
        await first.SyncAsync();
        await second.SyncAsync();

        Assert.Equal(["teh=THE@Folder"], Summary(second));
    }

    [Fact]
    public async Task Concurrent_edits_keep_the_newer_one_and_a_copy_of_the_other()
    {
        var server = new FakeServer();
        var first = new Device(server, NewSnippet("teh", "the"));
        var second = new Device(server);
        await first.SyncAsync();
        await second.SyncAsync();

        first.Edit("teh", s => s with { Expansion = "from first", Modified = new DateTime(2026, 3, 1) });
        second.Edit("teh", s => s with { Expansion = "from second", Modified = new DateTime(2026, 3, 2) });
        await first.SyncAsync();
        var outcome = await second.SyncAsync();
        await first.SyncAsync();

        Assert.Equal(1, outcome.ConflictCopies);
        string[] expected = ["teh=from first@Conflicts", "teh=from second@Folder"];
        Assert.Equal(expected, Summary(second));
        Assert.Equal(expected, Summary(first));
    }

    [Fact]
    public async Task An_edit_survives_a_delete_from_another_device()
    {
        var server = new FakeServer();
        var first = new Device(server, NewSnippet("teh", "the"));
        var second = new Device(server);
        await first.SyncAsync();
        await second.SyncAsync();

        first.Snippets.Clear();
        second.Edit("teh", s => s with { Expansion = "kept", Modified = new DateTime(2026, 4, 1) });
        await first.SyncAsync();
        await second.SyncAsync();
        await first.SyncAsync();

        Assert.Equal(["teh=kept@Folder"], Summary(first));
        Assert.Equal(["teh=kept@Folder"], Summary(second));
        Assert.False(server.Items.Values.Single().Deleted);
    }

    [Fact]
    public async Task Two_devices_with_the_same_imported_library_do_not_duplicate_it()
    {
        var server = new FakeServer();
        var first = new Device(server, NewSnippet("teh", "the"), NewSnippet("adn", "and"));
        var second = new Device(server, NewSnippet("teh", "the"), NewSnippet("adn", "and"), NewSnippet("only", "on second"));
        await first.SyncAsync();

        var outcome = await second.SyncAsync();
        await first.SyncAsync();

        Assert.Equal(1, outcome.Uploaded);
        Assert.Equal(3, server.Items.Count);
        Assert.Equal(Summary(first), Summary(second));
    }

    [Fact]
    public async Task The_vault_header_syncs_so_protected_snippets_open_on_the_other_device()
    {
        var server = new FakeServer();
        var (header, vault) = Vault.Create("master password", 1_000);
        var secret = SnippetProtection.Protect(NewSnippet("pw", "hunter2"), vault);
        var first = new Device(server, secret) { Vault = header };
        var second = new Device(server);
        await first.SyncAsync();

        var outcome = await second.SyncAsync();

        Assert.True(outcome.VaultReplaced);
        Assert.Equal(header, second.Vault);
        using var unlocked = Vault.TryUnlock(second.Vault!, "master password");
        Assert.Equal("hunter2", SnippetProtection.Reveal(second.Snippets.Single(), unlocked!));
        Assert.DoesNotContain(server.Items.Values, i => i.Ciphertext!.Contains("hunter2"));
    }

    [Fact]
    public async Task Different_master_passwords_on_two_devices_are_reported_not_overwritten()
    {
        var server = new FakeServer();
        var first = new Device(server) { Vault = Vault.Create("first password", 1_000).Header };
        var secondHeader = Vault.Create("second password", 1_000).Header;
        var second = new Device(server) { Vault = secondHeader };
        await first.SyncAsync();

        var outcome = await second.SyncAsync();

        Assert.True(outcome.VaultConflict);
        Assert.Equal(secondHeader, second.Vault);
    }

    [Fact]
    public async Task Items_sealed_with_another_key_are_skipped_not_fatal()
    {
        var server = new FakeServer();
        var foreignKey = AccountCrypto.ItemKey(RandomNumberGenerator.GetBytes(32));
        var foreign = new Device(server, NewSnippet("x", "y"));
        await new SyncEngine(server, foreignKey).RunAsync(foreign.Snippets, null, new SyncState(), CancellationToken.None);

        var outcome = await new Device(server, NewSnippet("teh", "the")).SyncAsync();

        Assert.Equal(1, outcome.Unreadable);
        Assert.Equal(1, outcome.Uploaded);
    }

    [Fact]
    public void Snippets_round_trip_through_item_json()
    {
        var (_, vault) = Vault.Create("master password", 1_000);
        var plain = NewSnippet("teh", "the ünïcode") with { Description = "d", Case = CaseMode.Exact, Trigger = TriggerFlags.Immediate };
        var protectedOne = SnippetProtection.Protect(NewSnippet("pw", "secret"), vault);

        foreach (var snippet in new[] { plain, protectedOne })
        {
            var back = SyncItems.ToSnippet(snippet.Id, SyncItems.FromSnippet(snippet))!;
            Assert.Equal(SyncItems.Hash(SyncItems.FromSnippet(snippet)), SyncItems.Hash(SyncItems.FromSnippet(back)));
            Assert.Equal(snippet.Protected, back.Protected);
            Assert.Equal(snippet.Encrypted, back.Encrypted);
        }
    }
}
