using System.Text.Json;
using System.Text.Json.Serialization;
using Freevy.Core.Backup;
using Freevy.Core.Security;
using Freevy.Core.Sync;

namespace Freevy.Core;

/// <summary>
/// Freevy's own data folder: snippets.json, settings.json, vault.json and backup-state.json.
/// Writes go through a temporary file so a crash never leaves a half-written file behind.
/// </summary>
public sealed class DataStore(string directory)
{
    public const string SnippetsFileName = "snippets.json";
    public const string SettingsFileName = "settings.json";
    public const string VaultFileName = "vault.json";
    public const string BackupStateFileName = "backup-state.json";
    public const string SyncStateFileName = "sync-state.json";
    public const string StatsFileName = "stats.json";

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Local files only, never embedded in HTML, so keep characters like < and " readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Freevy");

    public string Directory { get; } = directory;

    public string SnippetsPath => Path.Combine(Directory, SnippetsFileName);

    public string SettingsPath => Path.Combine(Directory, SettingsFileName);

    public string VaultPath => Path.Combine(Directory, VaultFileName);

    public string BackupStatePath => Path.Combine(Directory, BackupStateFileName);

    public string SyncStatePath => Path.Combine(Directory, SyncStateFileName);

    public string DefaultBackupDirectory => Path.Combine(Directory, "backups");

    public bool HasSnippets => File.Exists(SnippetsPath);

    public IReadOnlyList<Snippet> LoadSnippets()
    {
        var snippets = Read<List<Snippet>>(SnippetsPath) ?? [];

        // Files from before snippets had ids get them once, so sync can track every snippet.
        if (snippets.Any(s => s.Id == Guid.Empty))
        {
            snippets = snippets.Select(s => s.Id == Guid.Empty ? s with { Id = Guid.NewGuid() } : s).ToList();
            SaveSnippets(snippets);
        }

        return snippets;
    }

    public Settings LoadSettings() => MigrateSettings(Read<Settings>(SettingsPath) ?? new Settings());

    /// <summary>Defaults saved under the app's old name (OzanExtender) become today's defaults.</summary>
    private static Settings MigrateSettings(Settings settings) =>
        settings.Backup.Sftp.RemoteDirectory == "OzanExtender-backups"
            ? settings with { Backup = settings.Backup with { Sftp = settings.Backup.Sftp with { RemoteDirectory = new SftpSettings().RemoteDirectory } } }
            : settings;

    public VaultHeader? LoadVault() => Read<VaultHeader>(VaultPath);

    public BackupState LoadBackupState() => Read<BackupState>(BackupStatePath) ?? new BackupState();

    public SyncState LoadSyncState() => Read<SyncState>(SyncStatePath) ?? new SyncState();

    public UsageStats LoadStats() => Read<UsageStats>(Path.Combine(Directory, StatsFileName)) ?? new UsageStats();

    public void SaveStats(UsageStats stats) => Write(Path.Combine(Directory, StatsFileName), stats);

    public void SaveSyncState(SyncState state) => Write(SyncStatePath, state);

    public void DeleteSyncState() => File.Delete(SyncStatePath);

    public void SaveSnippets(IReadOnlyList<Snippet> snippets) => Write(SnippetsPath, snippets);

    public void SaveSettings(Settings settings) => Write(SettingsPath, settings);

    public void SaveVault(VaultHeader header) => Write(VaultPath, header);

    public void DeleteVault() => File.Delete(VaultPath);

    public void SaveBackupState(BackupState state) => Write(BackupStatePath, state);

    /// <summary>Copies the current snippets file aside, e.g. before an import replaces it.</summary>
    public string? BackupSnippets()
    {
        if (!File.Exists(SnippetsPath))
        {
            return null;
        }

        var backup = Path.Combine(Directory, $"snippets.{DateTime.Now:yyyyMMdd-HHmmss}.bak.json");
        File.Copy(SnippetsPath, backup);
        return backup;
    }

    private static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;

    private void Write<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
