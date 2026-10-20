using System.Security.Cryptography;
using Freevy.Core.Security;

namespace Freevy.Core.Sync;

/// <summary>What the device last agreed on with the server for one item. A null hash means deleted.</summary>
public sealed record SyncedEntry(long Revision, string? Hash);

/// <summary>Kept in sync-state.json next to the snippets.</summary>
public sealed record SyncState
{
    public long Cursor { get; init; }

    public Dictionary<Guid, SyncedEntry> Items { get; init; } = [];

    public DateTime? LastSyncUtc { get; init; }
}

public sealed record SyncOutcome(
    IReadOnlyList<Snippet> Snippets,
    VaultHeader? Vault,
    SyncState State,
    int Downloaded,
    int Uploaded,
    int ConflictCopies,
    bool VaultReplaced,
    bool VaultConflict,
    int Unreadable);

/// <summary>
/// One sync pass: pull remote changes and merge them into the local snippets, then push local
/// changes. Local changes are found by comparing each item's hash with the hash recorded at the
/// last sync, so the editor needs no change tracking of its own.
/// </summary>
public sealed class SyncEngine(ISyncTransport transport, byte[] itemKey)
{
    public const string ConflictFolder = "Conflicts";
    private const int BatchSize = 500;
    private const int MaxPushRounds = 3;

    private List<Snippet> _snippets = [];
    private VaultHeader? _vault;
    private Dictionary<Guid, SyncedEntry> _entries = [];
    private int _downloaded;
    private int _copies;
    private int _unreadable;
    private bool _vaultReplaced;
    private bool _vaultConflict;

    public async Task<SyncOutcome> RunAsync(IReadOnlyList<Snippet> snippets, VaultHeader? vault, SyncState state, CancellationToken cancellationToken)
    {
        _snippets = [.. snippets];
        _vault = vault;
        _entries = new Dictionary<Guid, SyncedEntry>(state.Items);
        var firstSync = state.Cursor == 0 && state.Items.Count == 0;

        var cursor = state.Cursor;
        var remote = new List<RemoteItem>();
        PullPage page;
        do
        {
            page = await transport.PullAsync(cursor, cancellationToken);
            remote.AddRange(page.Items);
            cursor = page.Cursor;
        }
        while (page.More);

        if (firstSync)
        {
            AdoptMatchingIds(remote);
        }

        foreach (var item in remote)
        {
            ApplyRemote(item);
        }

        var uploaded = 0;
        for (var round = 0; round < MaxPushRounds; round++)
        {
            var changes = LocalChanges();
            if (changes.Count == 0)
            {
                break;
            }

            var conflicts = new List<RemoteItem?>();
            foreach (var batch in changes.Chunk(BatchSize))
            {
                var results = await transport.PushAsync(batch.Select(c => c.Change).ToList(), cancellationToken);
                var hashes = batch.ToDictionary(c => c.Change.Id, c => c.Hash);
                foreach (var result in results)
                {
                    if (result.Accepted)
                    {
                        uploaded++;
                        if (result.Revision is { } revision)
                        {
                            _entries[result.Id] = new SyncedEntry(revision, hashes[result.Id]);
                        }
                        else
                        {
                            // Deleting something the server never had: nothing left to track.
                            _entries.Remove(result.Id);
                        }
                    }
                    else if (result.Current is { } current)
                    {
                        conflicts.Add(current);
                    }
                    else
                    {
                        // The server no longer has the item (for example after an erase): push it as new.
                        _entries.Remove(result.Id);
                    }
                }
            }

            foreach (var current in conflicts.OfType<RemoteItem>())
            {
                ApplyRemote(current);
            }
        }

        return new SyncOutcome(
            _snippets,
            _vault,
            new SyncState { Cursor = cursor, Items = _entries, LastSyncUtc = DateTime.UtcNow },
            _downloaded,
            uploaded,
            _copies,
            _vaultReplaced,
            _vaultConflict,
            _unreadable);
    }

    /// <summary>
    /// The first time a device syncs, identical snippets it already has (for example from the
    /// same Breevy import on two PCs) take the server's ids instead of being uploaded twice.
    /// </summary>
    private void AdoptMatchingIds(List<RemoteItem> remote)
    {
        var byContent = new Dictionary<string, Guid>();
        foreach (var item in remote.Where(i => !i.Deleted && i.Id != SyncItems.VaultId))
        {
            if (Decode(item) is { } snippet)
            {
                byContent.TryAdd(ContentKey(snippet), item.Id);
            }
        }

        var remoteIds = remote.Select(i => i.Id).ToHashSet();
        for (var i = 0; i < _snippets.Count; i++)
        {
            var local = _snippets[i];
            if (!remoteIds.Contains(local.Id) && byContent.Remove(ContentKey(local), out var serverId))
            {
                _snippets[i] = local with { Id = serverId };
            }
        }
    }

    private static string ContentKey(Snippet snippet) =>
        string.Join('\u0001', [string.Join('|', snippet.Abbreviations), snippet.Protected ? snippet.Encrypted ?? "" : snippet.Expansion, snippet.Folder]);

    private void ApplyRemote(RemoteItem item)
    {
        // Already seen, typically this device's own earlier push coming back in a pull.
        if (_entries.TryGetValue(item.Id, out var seen) && item.Revision <= seen.Revision)
        {
            return;
        }

        if (item.Id == SyncItems.VaultId)
        {
            ApplyRemoteVault(item);
            return;
        }

        var index = _snippets.FindIndex(s => s.Id == item.Id);
        var local = index >= 0 ? _snippets[index] : null;
        var localHash = local is null ? null : SyncItems.Hash(SyncItems.FromSnippet(local));
        var known = _entries.GetValueOrDefault(item.Id);
        var localUnchanged = local is not null && known?.Hash == localHash;

        if (item.Deleted)
        {
            if (local is not null && localUnchanged)
            {
                _snippets.RemoveAt(index);
                _downloaded++;
            }

            // An edited local copy survives a remote delete and is pushed again as the new version.
            _entries[item.Id] = new SyncedEntry(item.Revision, null);
            return;
        }

        var remote = Decode(item);
        if (remote is null)
        {
            _unreadable++;
            return;
        }

        var remoteHash = SyncItems.Hash(SyncItems.FromSnippet(remote));
        if (local is null)
        {
            _snippets.Add(remote);
            _downloaded++;
        }
        else if (localHash != remoteHash)
        {
            if (localUnchanged || IsNewer(remote, local))
            {
                if (!localUnchanged && ExpansionDiffers(local, remote))
                {
                    _snippets.Add(ConflictCopy(local));
                }

                _snippets[index] = remote;
                _downloaded++;
            }
            else if (ExpansionDiffers(local, remote))
            {
                _snippets.Add(ConflictCopy(remote));
                _downloaded++;
            }
        }

        _entries[item.Id] = new SyncedEntry(item.Revision, remoteHash);
    }

    private void ApplyRemoteVault(RemoteItem item)
    {
        var known = _entries.GetValueOrDefault(item.Id);
        var localHash = _vault is null ? null : SyncItems.Hash(SyncItems.FromVault(_vault));
        var localUnchanged = known?.Hash == localHash;

        if (item.Deleted)
        {
            _entries[item.Id] = new SyncedEntry(item.Revision, null);
            return;
        }

        VaultHeader? remote;
        try
        {
            remote = SyncItems.ToVault(AccountCrypto.OpenItem(itemKey, item.Id, item.Ciphertext ?? ""));
        }
        catch (CryptographicException)
        {
            remote = null;
        }

        if (remote is null)
        {
            _unreadable++;
            return;
        }

        var remoteHash = SyncItems.Hash(SyncItems.FromVault(remote));
        if (_vault is null || (localHash != remoteHash && localUnchanged))
        {
            _vault = remote;
            _vaultReplaced = true;
        }
        else if (localHash != remoteHash)
        {
            // Two different master passwords: keep this device's; the user must resolve it.
            _vaultConflict = true;
        }

        _entries[item.Id] = new SyncedEntry(item.Revision, remoteHash);
    }

    private List<(PushChange Change, string? Hash)> LocalChanges()
    {
        var changes = new List<(PushChange, string?)>();
        var present = new HashSet<Guid>();

        foreach (var snippet in _snippets)
        {
            present.Add(snippet.Id);
            var json = SyncItems.FromSnippet(snippet);
            var hash = SyncItems.Hash(json);
            var known = _entries.GetValueOrDefault(snippet.Id);
            if (known?.Hash != hash)
            {
                changes.Add((new PushChange(snippet.Id, known?.Revision, AccountCrypto.SealItem(itemKey, snippet.Id, json), false), hash));
            }
        }

        if (_vault is not null)
        {
            present.Add(SyncItems.VaultId);
            var json = SyncItems.FromVault(_vault);
            var hash = SyncItems.Hash(json);
            var known = _entries.GetValueOrDefault(SyncItems.VaultId);
            if (known?.Hash != hash && !_vaultConflict)
            {
                changes.Add((new PushChange(SyncItems.VaultId, known?.Revision, AccountCrypto.SealItem(itemKey, SyncItems.VaultId, json), false), hash));
            }
        }

        foreach (var (id, entry) in _entries)
        {
            if (entry.Hash is not null && !present.Contains(id))
            {
                changes.Add((new PushChange(id, entry.Revision, null, true), null));
            }
        }

        return changes;
    }

    private Snippet? Decode(RemoteItem item)
    {
        try
        {
            return SyncItems.ToSnippet(item.Id, AccountCrypto.OpenItem(itemKey, item.Id, item.Ciphertext ?? ""));
        }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private Snippet ConflictCopy(Snippet loser)
    {
        _copies++;
        var description = loser.Description.Length > 0 ? $"Conflict copy: {loser.Description}" : "Conflict copy";
        return loser with { Id = Guid.NewGuid(), Folder = ConflictFolder, Description = description };
    }

    private static bool IsNewer(Snippet a, Snippet b) => (a.Modified ?? DateTime.MinValue) > (b.Modified ?? DateTime.MinValue);

    private static bool ExpansionDiffers(Snippet a, Snippet b) =>
        a.Protected != b.Protected || (a.Protected ? a.Encrypted != b.Encrypted : a.Expansion != b.Expansion);
}
