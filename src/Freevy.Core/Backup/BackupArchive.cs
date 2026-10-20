using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Freevy.Core.Backup;

/// <summary>
/// A backup is a zip with snippets.json and, when a master password is set, vault.json.
/// Protected expansions stay encrypted inside it; settings are not included because they
/// hold machine-bound secrets (DPAPI) and backup destinations.
/// </summary>
public static class BackupArchive
{
    public const string FilePrefix = "Freevy-backup-";

    // Backups made before the rename keep their old prefix and still count for retention.
    private const string LegacyFilePrefix = "OzanExtender-backup-";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    public static string FileName(DateTime localTime) =>
        FilePrefix + localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".zip";

    public static bool IsBackupFileName(string name) => Timestamp(name) is not null;

    /// <summary>The oldest backups: everything after the newest <paramref name="keep"/>, ordered by the time in the name.</summary>
    public static IEnumerable<string> SelectExpired(IEnumerable<string> names, int keep) =>
        names.Select(n => (Name: n, Time: Timestamp(n)))
            .Where(x => x.Time is not null)
            .OrderByDescending(x => x.Time)
            .Skip(Math.Max(1, keep))
            .Select(x => x.Name);

    private static DateTime? Timestamp(string name)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var prefix in new[] { FilePrefix, LegacyFilePrefix })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal)
                && DateTime.TryParseExact(name[prefix.Length..^4], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return time;
            }
        }

        return null;
    }

    public static void Create(DataStore store, string zipPath)
    {
        if (!File.Exists(store.SnippetsPath))
        {
            throw new InvalidOperationException("There are no snippets to back up yet.");
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(store.SnippetsPath, DataStore.SnippetsFileName, CompressionLevel.Optimal);
        if (File.Exists(store.VaultPath))
        {
            zip.CreateEntryFromFile(store.VaultPath, DataStore.VaultFileName, CompressionLevel.Optimal);
        }
    }

    /// <summary>Validates the archive, then replaces the snippets and vault in <paramref name="store"/>.</summary>
    public static int Restore(string zipPath, DataStore store)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var snippetsEntry = zip.GetEntry(DataStore.SnippetsFileName)
            ?? throw new InvalidDataException("This file is not an Freevy backup (snippets.json is missing).");
        var snippetsJson = ReadEntry(snippetsEntry);
        var snippets = JsonSerializer.Deserialize<List<Snippet>>(snippetsJson, DataStore.Options)
            ?? throw new InvalidDataException("The backup's snippets.json is empty.");

        var vaultEntry = zip.GetEntry(DataStore.VaultFileName);
        var vault = vaultEntry is null ? null : JsonSerializer.Deserialize<Security.VaultHeader>(ReadEntry(vaultEntry), DataStore.Options);
        if (vault is null && snippets.Any(s => s.Protected))
        {
            throw new InvalidDataException("The backup has protected snippets but no vault.json, so they could not be decrypted.");
        }

        store.SaveSnippets(snippets);
        if (vault is null)
        {
            store.DeleteVault();
        }
        else
        {
            store.SaveVault(vault);
        }

        return snippets.Count;
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
