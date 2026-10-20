namespace Freevy.Core.Sync;

public sealed record RemoteItem(Guid Id, long Revision, bool Deleted, string? Ciphertext);

/// <summary>An earlier version of an item the server kept; still sealed with the item key.</summary>
public sealed record HistoryVersion(Guid Id, long Revision, string Ciphertext, DateTimeOffset ReplacedAt, bool WasDeleted);

public sealed record PullPage(IReadOnlyList<RemoteItem> Items, long Cursor, bool More);

/// <param name="BaseRevision">Revision this change is based on; null for an item the server has never seen.</param>
public sealed record PushChange(Guid Id, long? BaseRevision, string? Ciphertext, bool Deleted);

/// <param name="Current">On a conflict, the server's version of the item, or null when it does not exist.</param>
public sealed record PushResult(Guid Id, bool Accepted, long? Revision, RemoteItem? Current);

/// <summary>The sync endpoints of the server; an interface so the engine can be tested without one.</summary>
public interface ISyncTransport
{
    Task<PullPage> PullAsync(long since, CancellationToken cancellationToken);

    Task<IReadOnlyList<PushResult>> PushAsync(IReadOnlyList<PushChange> changes, CancellationToken cancellationToken);
}

/// <summary>The server rejected the device token: the device was signed out or the password changed.</summary>
public sealed class SignedOutException(string message) : Exception(message);
