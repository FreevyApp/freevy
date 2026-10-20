using System.Diagnostics;
using System.Text.Json;
using Freevy.Core;
using Freevy.Core.Backup;
using Freevy.Core.Sync;

namespace Freevy.App;

/// <summary>
/// Request/response bridge between the web UI and <see cref="AppCore"/>.
/// The UI posts { id, method, params }; the answer is { id, ok, result } or { id, ok: false, error }.
/// Unsolicited { event, data } messages tell the UI that something changed.
/// </summary>
internal sealed class Bridge(AppCore core, IWin32Window owner, Action<string> windowAction, Action<string> post)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Decrypted versions from the last history request; restore picks from these by key.
    private Dictionary<string, Snippet> _history = [];

    public void Emit(string name, object? data = null) =>
        post(JsonSerializer.Serialize(new { @event = name, data }, Json));

    public async Task HandleAsync(string message)
    {
        JsonElement request;
        try
        {
            request = JsonDocument.Parse(message).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        var id = request.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
        var method = request.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        var args = request.TryGetProperty("params", out var p) ? p : default;
        try
        {
            var result = await DispatchAsync(method, args);
            post(JsonSerializer.Serialize(new { id, ok = true, result }, Json));
        }
        catch (Exception ex) when (ex is UserError or FreevyApiException or InvalidOperationException or IOException or UnauthorizedAccessException
                                   or HttpRequestException or TaskCanceledException or NotSupportedException or Renci.SshNet.Common.SshException
                                   or System.Net.Sockets.SocketException)
        {
            var text = ex switch
            {
                HttpRequestException or TaskCanceledException => "Could not reach the server. Check your connection and the server address.",
                System.Net.Sockets.SocketException => $"Could not connect: {ex.Message}",
                _ => ex.Message,
            };
            post(JsonSerializer.Serialize(new { id, ok = false, error = text }, Json));
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement a)
    {
        switch (method)
        {
            case "getState": return State();
            case "getSnippets": return core.Snippets.Select(SnippetDto).ToList();

            case "saveSnippet":
                return SnippetDto(core.SaveSnippet(
                    a.TryGetProperty("id", out var sid) && sid.ValueKind == JsonValueKind.String ? Guid.Parse(sid.GetString()!) : null,
                    a.GetProperty("abbreviations").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                    a.TryGetProperty("expansion", out var exp) && exp.ValueKind == JsonValueKind.String ? exp.GetString() : null,
                    Text(a, "description"),
                    Text(a, "folder"),
                    TriggerFrom(Text(a, "trigger"), Bool(a, "wordStart")),
                    CaseFrom(Text(a, "case")),
                    Bool(a, "protected")));
            case "deleteSnippets":
                core.DeleteSnippets(a.GetProperty("ids").EnumerateArray().Select(x => Guid.Parse(x.GetString()!)).ToHashSet());
                return null;
            case "revealSnippet": return core.RevealSnippet(Guid.Parse(Text(a, "id")));
            case "renameFolder": return core.RenameFolder(Text(a, "folder"), Text(a, "name"));
            case "deleteFolder": return core.DeleteFolder(Text(a, "folder"));
            case "protectFolder": return core.SetFolderProtection(Text(a, "folder"), Bool(a, "protect"));

            case "unlock": return core.Unlock(Text(a, "password"), a.TryGetProperty("minutes", out var min) ? min.GetInt32() : null);
            case "lock": core.Lock(); return null;
            case "setMasterPassword":
                return core.SetMasterPassword(
                    a.TryGetProperty("current", out var cur) ? cur.GetString() : null,
                    Text(a, "password"),
                    Bool(a, "protectPasswordFolders"));
            case "removeMasterPassword": return core.RemoveMasterPassword(Text(a, "password"));

            case "saveSettings": core.SaveSettings(ApplySettings(core.Settings, a)); return State();
            case "setTheme": core.SaveSettings(core.Settings with { Theme = Text(a, "theme") }); return null;
            case "setAutostart": Autostart.Set(Bool(a, "enabled")); return Autostart.IsEnabled;

            case "backupNow": core.Backups.RunNow(); return null;
            case "restoreBackup": return core.RestoreBackup(Text(a, "path"));
            case "setSftpSecret": SetSftpSecrets(a); return State();
            case "testSftp": return await TestSftpAsync(a);
            case "trustHostKey":
                core.SaveSettings(core.Settings with { Backup = core.Settings.Backup with { Sftp = core.Settings.Backup.Sftp with { HostKeyFingerprint = Text(a, "fingerprint") } } });
                return State();

            case "chooseFolder": return ChooseFolder(Text(a, "title"), Text(a, "initial"));
            case "chooseFile": return ChooseFile(Text(a, "title"), Text(a, "filter"), Text(a, "initial"));
            case "exportSnippets": return ExportSnippets();
            case "importBreevy": return core.ImportBreevy();
            case "openDataFolder": core.OpenDataFolder(); return null;

            case "syncNow": core.Sync.SyncNow(); return null;
            case "getHistory":
                var history = await core.Sync.GetHistoryAsync();
                _history = history.ToDictionary(h => $"{h.Version.Id:D}:{h.Version.Revision}", h => h.Snippet);
                return history.Select(h => new
                {
                    key = $"{h.Version.Id:D}:{h.Version.Revision}",
                    replacedAt = h.Version.ReplacedAt.ToLocalTime().ToString("O"),
                    wasDeleted = h.Version.WasDeleted,
                    existsNow = core.Snippets.Any(s => s.Id == h.Version.Id),
                    snippet = SnippetDto(h.Snippet),
                }).ToList();
            case "restoreVersion":
                if (!_history.TryGetValue(Text(a, "key"), out var version))
                {
                    throw new UserError("That version is no longer available. Open the history again.");
                }

                return SnippetDto(core.RestoreVersion(version));
            case "signIn":
                await core.Sync.SignInAsync(new Uri(Text(a, "server")), Text(a, "email"), Text(a, "password"), Text(a, "device"), CancellationToken.None);
                return State();
            case "signOut": await core.Sync.SignOutAsync(); return State();
            case "signInWithBrowser":
                // Automated UI tests (--offscreen) open the page themselves instead of the default browser.
                var browserSignIn = core.Sync.BeginBrowserSignIn(new Uri(Text(a, "server")), Text(a, "device"), openBrowser: !WebWindow.Offscreen);
                return new { fingerprint = browserSignIn.Fingerprint, url = WebWindow.Offscreen ? browserSignIn.BrowserUrl.AbsoluteUri : null };
            case "cancelBrowserSignIn": core.Sync.CancelBrowserSignIn(); return null;
            case "openAccountPage":
                // Only the account's own server, and only these two pages.
                var page = Text(a, "path") == "register" ? "register" : "account";
                Process.Start(new ProcessStartInfo($"{core.Settings.Sync.ServerUrl.TrimEnd('/')}/{page}") { UseShellExecute = true });
                return null;

            case "window": windowAction(Text(a, "action")); return null;
            default: throw new UserError($"Unknown request '{method}'.");
        }
    }

    // ---- State for the UI ---------------------------------------------------

    public object State()
    {
        var s = core.Settings;
        var today = core.Stats.For(DateOnly.FromDateTime(DateTime.Now));
        var backupState = core.Backups.State;
        var sync = s.Sync;
        return new
        {
            version = typeof(Bridge).Assembly.GetName().Version?.ToString(3),
            dataDirectory = core.Store.Directory,
            breevyAvailable = core.BreevyAvailable,
            settings = new
            {
                s.ExpansionEnabled,
                delimiters = Settings.EscapeDelimiters(s.Delimiters),
                s.ClipboardThreshold,
                defaultTrigger = s.DefaultTrigger.HasFlag(TriggerFlags.Immediate) ? "instant" : "after",
                defaultWordStart = s.DefaultTrigger.HasFlag(TriggerFlags.WordStart),
                defaultCase = CaseName(s.DefaultCase),
                s.MaskFoldersContaining,
                s.UnlockMinutes,
                s.LockWhenWindowsLocks,
                s.Theme,
                autostart = Autostart.IsEnabled,
                backup = new
                {
                    s.Backup.Enabled,
                    s.Backup.IntervalHours,
                    s.Backup.OnChange,
                    s.Backup.KeepCount,
                    s.Backup.LocalEnabled,
                    localDirectory = core.BackupDirectory,
                    s.Backup.FolderEnabled,
                    s.Backup.FolderDirectory,
                    sftp = new
                    {
                        s.Backup.Sftp.Enabled,
                        s.Backup.Sftp.Host,
                        s.Backup.Sftp.Port,
                        s.Backup.Sftp.Username,
                        hasPassword = s.Backup.Sftp.ProtectedPassword.Length > 0,
                        s.Backup.Sftp.PrivateKeyPath,
                        hasPassphrase = s.Backup.Sftp.ProtectedKeyPassphrase.Length > 0,
                        s.Backup.Sftp.RemoteDirectory,
                        s.Backup.Sftp.HostKeyFingerprint,
                    },
                },
                sync = new { sync.ServerUrl, sync.Email, sync.DeviceName, sync.IntervalMinutes, defaultServer = SyncSettings.DefaultServerUrl },
            },
            vault = new { configured = core.Vault.IsConfigured, unlocked = core.Vault.IsUnlocked, locksAt = core.Vault.LocksAtLocal?.ToString("HH:mm") },
            sync = new
            {
                signedIn = core.Sync.IsSignedIn,
                running = core.Sync.IsRunning,
                lastSync = core.Sync.LastSyncUtc?.ToLocalTime().ToString("O"),
                error = core.Sync.LastError,
            },
            backup = new
            {
                running = core.Backups.IsRunning,
                lastRun = backupState.LastRunUtc?.ToLocalTime().ToString("O"),
                lastSuccess = backupState.LastSuccessUtc?.ToLocalTime().ToString("O"),
                result = backupState.LastResult,
            },
            stats = new { today = new { today.Expansions, today.KeystrokesSaved }, core.Stats.TotalExpansions, core.Stats.TotalKeystrokesSaved },
            abbreviationCount = core.AbbreviationCount,
        };
    }

    public static object SnippetDto(Snippet s) => new
    {
        id = s.Id.ToString("D"),
        abbreviations = s.Abbreviations,
        expansion = s.Protected ? null : s.Expansion,
        description = s.Description,
        folder = s.Folder,
        trigger = s.Trigger.HasFlag(TriggerFlags.Immediate) ? "instant" : "after",
        wordStart = s.Trigger.HasFlag(TriggerFlags.WordStart),
        @case = CaseName(s.Case),
        @protected = s.Protected,
        modified = s.Modified?.ToString("O"),
    };

    // ---- Settings -----------------------------------------------------------

    private static Settings ApplySettings(Settings s, JsonElement a)
    {
        if (a.TryGetProperty("expansionEnabled", out var v)) s = s with { ExpansionEnabled = v.GetBoolean() };
        if (a.TryGetProperty("delimiters", out v))
        {
            var delimiters = Settings.UnescapeDelimiters(v.GetString() ?? "");
            s = s with { Delimiters = delimiters.Length > 0 ? delimiters : Settings.DefaultDelimiters };
        }

        if (a.TryGetProperty("clipboardThreshold", out v)) s = s with { ClipboardThreshold = Math.Clamp(v.GetInt32(), 1, 1_000_000) };
        if (a.TryGetProperty("defaultTrigger", out v) || a.TryGetProperty("defaultWordStart", out _))
        {
            var trigger = a.TryGetProperty("defaultTrigger", out var t) ? t.GetString() : (s.DefaultTrigger.HasFlag(TriggerFlags.Immediate) ? "instant" : "after");
            var wordStart = a.TryGetProperty("defaultWordStart", out var w) ? w.GetBoolean() : s.DefaultTrigger.HasFlag(TriggerFlags.WordStart);
            s = s with { DefaultTrigger = TriggerFrom(trigger, wordStart) };
        }

        if (a.TryGetProperty("defaultCase", out v)) s = s with { DefaultCase = CaseFrom(v.GetString()) };
        if (a.TryGetProperty("maskFoldersContaining", out v)) s = s with { MaskFoldersContaining = (v.GetString() ?? "").Trim() };
        if (a.TryGetProperty("unlockMinutes", out v)) s = s with { UnlockMinutes = Math.Clamp(v.GetInt32(), 1, 1440) };
        if (a.TryGetProperty("lockWhenWindowsLocks", out v)) s = s with { LockWhenWindowsLocks = v.GetBoolean() };
        if (a.TryGetProperty("syncIntervalMinutes", out v)) s = s with { Sync = s.Sync with { IntervalMinutes = Math.Clamp(v.GetInt32(), 1, 1440) } };

        if (a.TryGetProperty("backup", out var b))
        {
            var backup = s.Backup;
            if (b.TryGetProperty("enabled", out v)) backup = backup with { Enabled = v.GetBoolean() };
            if (b.TryGetProperty("intervalHours", out v)) backup = backup with { IntervalHours = Math.Clamp(v.GetInt32(), 0, 720) };
            if (b.TryGetProperty("onChange", out v)) backup = backup with { OnChange = v.GetBoolean() };
            if (b.TryGetProperty("keepCount", out v)) backup = backup with { KeepCount = Math.Clamp(v.GetInt32(), 1, 1000) };
            if (b.TryGetProperty("localEnabled", out v)) backup = backup with { LocalEnabled = v.GetBoolean() };
            if (b.TryGetProperty("localDirectory", out v)) backup = backup with { LocalDirectory = (v.GetString() ?? "").Trim() };
            if (b.TryGetProperty("folderEnabled", out v)) backup = backup with { FolderEnabled = v.GetBoolean() };
            if (b.TryGetProperty("folderDirectory", out v)) backup = backup with { FolderDirectory = (v.GetString() ?? "").Trim() };
            if (b.TryGetProperty("sftp", out var f))
            {
                var sftp = backup.Sftp;
                var hostBefore = (sftp.Host, sftp.Port);
                if (f.TryGetProperty("enabled", out v)) sftp = sftp with { Enabled = v.GetBoolean() };
                if (f.TryGetProperty("host", out v)) sftp = sftp with { Host = (v.GetString() ?? "").Trim() };
                if (f.TryGetProperty("port", out v)) sftp = sftp with { Port = Math.Clamp(v.GetInt32(), 1, 65535) };
                if (f.TryGetProperty("username", out v)) sftp = sftp with { Username = (v.GetString() ?? "").Trim() };
                if (f.TryGetProperty("privateKeyPath", out v)) sftp = sftp with { PrivateKeyPath = (v.GetString() ?? "").Trim() };
                if (f.TryGetProperty("remoteDirectory", out v)) sftp = sftp with { RemoteDirectory = (v.GetString() ?? "").Trim() is { Length: > 0 } dir ? dir : "Freevy-backups" };
                if ((sftp.Host, sftp.Port) != hostBefore)
                {
                    // A different server must be trusted again.
                    sftp = sftp with { HostKeyFingerprint = "" };
                }

                backup = backup with { Sftp = sftp };
            }

            s = s with { Backup = backup };
        }

        return s;
    }

    private void SetSftpSecrets(JsonElement a)
    {
        var sftp = core.Settings.Backup.Sftp;
        if (a.TryGetProperty("password", out var v)) sftp = sftp with { ProtectedPassword = Secrets.Protect(v.GetString() ?? "") };
        if (a.TryGetProperty("passphrase", out v)) sftp = sftp with { ProtectedKeyPassphrase = Secrets.Protect(v.GetString() ?? "") };
        core.SaveSettings(core.Settings with { Backup = core.Settings.Backup with { Sftp = sftp } });
    }

    private async Task<object> TestSftpAsync(JsonElement a)
    {
        var saved = core.Settings.Backup.Sftp;
        var draft = ApplySettings(core.Settings, a).Backup.Sftp;
        var connection = BackupManager.ToConnection(draft with
        {
            ProtectedPassword = a.TryGetProperty("password", out var pw) && pw.GetString() is { Length: > 0 } plain ? Secrets.Protect(plain) : saved.ProtectedPassword,
            ProtectedKeyPassphrase = a.TryGetProperty("passphrase", out var pp) && pp.GetString() is { Length: > 0 } phrase ? Secrets.Protect(phrase) : saved.ProtectedKeyPassphrase,
            HostKeyFingerprint = "",
        });
        if (connection.Host.Length == 0 || connection.Username.Length == 0)
        {
            throw new UserError("Enter the host and user first.");
        }

        var fingerprint = await Task.Run(() => SftpBackupTarget.Probe(connection));
        var sameServer = draft.Host == saved.Host && draft.Port == saved.Port;
        return new { fingerprint, trusted = sameServer && fingerprint == saved.HostKeyFingerprint, changed = sameServer && saved.HostKeyFingerprint.Length > 0 && fingerprint != saved.HostKeyFingerprint };
    }

    // ---- Native dialogs -------------------------------------------------------

    private string? ChooseFolder(string title, string initial)
    {
        using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, SelectedPath = initial };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private string? ChooseFile(string title, string filter, string initial)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = filter.Length > 0 ? filter : "All files (*.*)|*.*" };
        if (initial.Length > 0 && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    private string? ExportSnippets()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export snippets",
            Filter = "JSON files (*.json)|*.json",
            FileName = $"Freevy-snippets-{DateTime.Now:yyyyMMdd}.json",
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        core.ExportSnippets(dialog.FileName);
        return dialog.FileName;
    }

    // ---- Helpers ----------------------------------------------------------------

    private static string Text(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static TriggerFlags TriggerFrom(string? trigger, bool wordStart) =>
        (trigger == "instant" ? TriggerFlags.Immediate : TriggerFlags.AfterDelimiter) | (wordStart ? TriggerFlags.WordStart : TriggerFlags.None);

    private static CaseMode CaseFrom(string? name) => name switch
    {
        "exact" => CaseMode.Exact,
        "ignore" => CaseMode.IgnoreCase,
        _ => CaseMode.Adapt,
    };

    private static string CaseName(CaseMode mode) => mode switch
    {
        CaseMode.Exact => "exact",
        CaseMode.IgnoreCase => "ignore",
        _ => "adapt",
    };
}
