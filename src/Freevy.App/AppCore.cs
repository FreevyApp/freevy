using System.Diagnostics;
using System.Security.Cryptography;
using Freevy.Core;
using Freevy.Core.Backup;
using Freevy.Core.Import.Breevy;
using Freevy.Core.Security;
using Freevy.Core.Macros;
using Freevy.Core.Sync;
using Microsoft.Win32;

namespace Freevy.App;

/// <summary>A problem the user should read, shown as-is in the UI.</summary>
internal sealed class UserError(string message) : Exception(message);

/// <summary>What changed, so the UI only refreshes what it needs.</summary>
internal enum Change { Snippets, Settings, Vault, Sync, Backup, Stats }

/// <summary>
/// Everything Freevy does, without any UI: expansion, snippets, vault, backups, sync, stats
/// and settings. Windows (the WebView UI, the tray) call these methods and listen to the events.
/// All members run on the UI thread.
/// </summary>
internal sealed class AppCore : IDisposable
{
    private readonly DataStore _store;
    private readonly SynchronizationContext _ui;
    private readonly InputHooks _hooks;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 3000 };
    private Settings _settings = new();
    private IReadOnlyList<Snippet> _snippets = [];
    private ExpansionEngine _engine = new([], Settings.DefaultDelimiters);
    private OutputSender _output = new(new Settings().ClipboardThreshold, new Settings().LineBreaks);
    private UsageStats _stats;
    private bool _statsDirty;
    private bool _busy;

    public AppCore(DataStore store, SynchronizationContext ui)
    {
        _store = store;
        _ui = ui;
        Vault = new VaultSession(store);
        Backups = new BackupManager(store, () => _settings, ui);
        Sync = new SyncManager(store, () => _settings, SaveSettingsAndReload, ui);
        _stats = store.LoadStats();

        Directory.CreateDirectory(store.Directory);
        if (!store.HasSnippets && BreevyImporter.IsAvailable(BreevyImporter.DefaultDirectory))
        {
            ImportBreevy(replaceExisting: false);
        }
        else
        {
            Reload();
        }

        _hooks = new InputHooks();
        _hooks.CharTyped += OnChar;
        _hooks.Backspace += () => _engine.Backspace();
        _hooks.ContextLost += () => _engine.Reset();

        Vault.Changed += () => Changed?.Invoke(Change.Vault);
        Backups.Completed += result =>
        {
            Changed?.Invoke(Change.Backup);
            if (!result.Success)
            {
                Notice?.Invoke(result.Summary, true);
            }
        };
        Sync.StatusChanged += () => Changed?.Invoke(Change.Sync);
        WireUpdates();
        Sync.SignedOut += message => Notice?.Invoke(message, true);
        Sync.Applied += OnSyncApplied;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // Hand edits of the JSON files are picked up too.
        _watcher = new FileSystemWatcher(store.Directory, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, e) => OnDataFileChanged(e.Name);
        _watcher.Created += (_, e) => OnDataFileChanged(e.Name);
        _watcher.Renamed += (_, e) => OnDataFileChanged(e.Name);
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            Reload();
        };
        _statsTimer.Tick += (_, _) => FlushStats();
        _statsTimer.Start();
    }

    public event Action<Change>? Changed;

    /// <summary>A message for the user; the flag marks problems.</summary>
    public event Action<string, bool>? Notice;

    /// <summary>Set by the UI layer: asks for the master password; returns whether the vault is now unlocked.</summary>
    public Func<string, Task<bool>>? RequestUnlock { get; set; }

    /// <summary>Asks for %(Input)/%(Fill) values before typing; null means the user cancelled.</summary>
    public Func<IReadOnlyList<InputRequest>, Task<IReadOnlyDictionary<string, string>?>>? RequestInputs { get; set; }

    public VaultSession Vault { get; }

    public BackupManager Backups { get; }

    public SyncManager Sync { get; }

    /// <summary>Updates from freevy.app; inactive unless Freevy was installed with its installer.</summary>
    public AppUpdater Updates { get; } = new();

    private void WireUpdates()
    {
        Updates.Enabled = () => _settings.AutoUpdate;
        Updates.AutoInstall = () => _settings.AutoInstallUpdates;
        Updates.Interval = () => TimeSpan.FromHours(Math.Max(1, _settings.UpdateCheckHours));
        Updates.LastCheckedUtc = _store.LoadLastUpdateCheck();
        Updates.Checked = utc => _store.SaveLastUpdateCheck(utc);
        Updates.StatusChanged += () => Changed?.Invoke(Change.Settings);
    }

    /// <summary>Shows a message in the window (or as a tray balloon while it is hidden).</summary>
    public void Notify(string message, bool problem = false) => Notice?.Invoke(message, problem);

    /// <summary>Asks the window to refresh after something outside the core changed, such as a ready update.</summary>
    public void RaiseChanged(Change change) => Changed?.Invoke(change);

    public Settings Settings => _settings;

    public IReadOnlyList<Snippet> Snippets => _snippets;

    public UsageStats Stats => _stats;

    public DataStore Store => _store;

    public int AbbreviationCount => _engine.AbbreviationCount;

    public string BackupDirectory =>
        _settings.Backup.LocalDirectory.Length > 0 ? _settings.Backup.LocalDirectory : _store.DefaultBackupDirectory;

    // ---- Snippets ---------------------------------------------------------

    public Snippet SaveSnippet(Guid? id, IReadOnlyList<string> abbreviations, string? expansion, string description, string folder,
        TriggerFlags trigger, CaseMode caseMode, bool isProtected, LineBreakMode? lineBreaks = null)
    {
        var cleaned = abbreviations.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (cleaned.Count == 0)
        {
            throw new UserError("Add at least one abbreviation.");
        }

        var previous = id is { } existing ? _snippets.FirstOrDefault(s => s.Id == existing) : null;
        var text = expansion?.Replace("\r\n", "\n");
        string? encrypted = null;

        if (isProtected)
        {
            if (text is null && previous is { Protected: true })
            {
                // The UI never saw the plain text (locked), so it cannot have changed.
                encrypted = previous.Encrypted;
            }
            else
            {
                if (!Vault.IsConfigured)
                {
                    throw new UserError("Set a master password first (Master password page), then save again.");
                }

                encrypted = UnlockedVault().Encrypt(text ?? "");
            }

            text = "";
        }
        else if (text is null)
        {
            // Turning protection off needs the plain text.
            text = previous is { Protected: true } ? SnippetProtection.Reveal(previous, UnlockedVault()) : previous?.Expansion ?? "";
        }

        var now = DateTime.Now;
        var snippet = new Snippet(cleaned, text, description.Trim(), caseMode, trigger, folder.Trim().Trim('/'),
            previous?.Created ?? now, now, isProtected, encrypted, previous?.Id ?? Guid.NewGuid(), lineBreaks);

        var list = _snippets.ToList();
        var index = previous is null ? -1 : list.FindIndex(s => s.Id == previous.Id);
        if (index >= 0)
        {
            list[index] = snippet;
        }
        else
        {
            list.Add(snippet);
        }

        SaveSnippets(list);
        return snippet;
    }

    /// <summary>Moves snippets into one folder ("" for none); returns how many actually moved.</summary>
    public int MoveSnippets(IReadOnlyCollection<Guid> ids, string folder)
    {
        var target = folder.Trim().Trim('/');
        var now = DateTime.Now;
        var moved = 0;
        var list = _snippets.Select(s =>
        {
            if (!ids.Contains(s.Id) || s.Folder == target)
            {
                return s;
            }

            moved++;
            return s with { Folder = target, Modified = now };
        }).ToList();

        if (moved > 0)
        {
            SaveSnippets(list);
        }

        return moved;
    }

    public void DeleteSnippets(IReadOnlyCollection<Guid> ids) =>
        SaveSnippets(_snippets.Where(s => !ids.Contains(s.Id)).ToList());

    /// <summary>Puts an earlier version back as the current one (or brings a deleted snippet back).</summary>
    public Snippet RestoreVersion(Snippet version)
    {
        var restored = version with { Modified = DateTime.Now };
        var list = _snippets.ToList();
        var index = list.FindIndex(s => s.Id == version.Id);
        if (index >= 0)
        {
            list[index] = restored;
        }
        else
        {
            list.Add(restored);
        }

        SaveSnippets(list);
        return restored;
    }

    public string RevealSnippet(Guid id)
    {
        var snippet = _snippets.FirstOrDefault(s => s.Id == id) ?? throw new UserError("That snippet no longer exists.");
        try
        {
            return SnippetProtection.Reveal(snippet, UnlockedVault());
        }
        catch (CryptographicException)
        {
            throw new UserError("This snippet could not be decrypted with the current master password.");
        }
    }

    public int RenameFolder(string folder, string newName)
    {
        newName = newName.Trim().Trim('/');
        if (newName.Length == 0)
        {
            throw new UserError("Enter a folder name.");
        }

        var changed = 0;
        var list = _snippets.Select(s =>
        {
            if (!InFolder(s, folder))
            {
                return s;
            }

            changed++;
            return s with { Folder = newName + s.Folder[folder.Length..] };
        }).ToList();
        SaveSnippets(list);
        return changed;
    }

    public int DeleteFolder(string folder)
    {
        var count = _snippets.Count(s => InFolder(s, folder));
        SaveSnippets(_snippets.Where(s => !InFolder(s, folder)).ToList());
        return count;
    }

    public int SetFolderProtection(string folder, bool protect)
    {
        var vault = UnlockedVault();
        var changed = 0;
        var list = _snippets.Select(s =>
        {
            if (!InFolder(s, folder) || s.Protected == protect)
            {
                return s;
            }

            changed++;
            return protect ? SnippetProtection.Protect(s, vault) : SnippetProtection.Unprotect(s, vault);
        }).ToList();
        SaveSnippets(list);
        return changed;
    }

    private static bool InFolder(Snippet snippet, string folder) =>
        snippet.Folder == folder || snippet.Folder.StartsWith(folder + "/", StringComparison.Ordinal);

    private void SaveSnippets(IReadOnlyList<Snippet> snippets)
    {
        _store.SaveSnippets(snippets);
        Backups.MarkChanged();
        Sync.NotifyLocalChange();
        Reload();
    }

    // ---- Vault --------------------------------------------------------------

    public bool Unlock(string password, int? minutes = null) => Vault.TryUnlock(password, minutes ?? _settings.UnlockMinutes);

    public void Lock() => Vault.Lock();

    /// <summary>Sets the first master password, or changes it and re-encrypts protected snippets.</summary>
    public int SetMasterPassword(string? currentPassword, string newPassword, bool protectPasswordFolders)
    {
        if (newPassword.Length < Core.Security.Vault.MinimumPasswordLength)
        {
            throw new UserError($"The master password needs at least {Core.Security.Vault.MinimumPasswordLength} characters.");
        }

        var header = _store.LoadVault();
        Vault? oldVault = null;
        if (header is not null)
        {
            oldVault = Core.Security.Vault.TryUnlock(header, currentPassword ?? "")
                ?? throw new UserError("The current master password is wrong. Nothing was changed.");
        }

        var (newHeader, newVault) = Core.Security.Vault.Create(newPassword);
        var snippets = _store.LoadSnippets();
        var protectedCount = 0;
        if (oldVault is not null)
        {
            using (oldVault)
            {
                snippets = SnippetProtection.Rekey(snippets, oldVault, newVault);
            }

            // Keep the previous pair so a crash between the two writes below can be recovered by hand.
            _store.BackupSnippets();
            File.Copy(_store.VaultPath, Path.Combine(_store.Directory, $"vault.{DateTime.Now:yyyyMMdd-HHmmss}.bak.json"), overwrite: true);
        }
        else if (protectPasswordFolders)
        {
            snippets = snippets.Select(s =>
            {
                if (s.Protected || !s.Folder.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }

                protectedCount++;
                return SnippetProtection.Protect(s, newVault);
            }).ToList();
        }

        _store.SaveSnippets(snippets);
        _store.SaveVault(newHeader);
        Vault.Adopt(newVault, _settings.UnlockMinutes);
        Backups.MarkChanged();
        Sync.NotifyLocalChange();
        Reload();
        return protectedCount;
    }

    public int RemoveMasterPassword(string password)
    {
        if (!Vault.IsConfigured)
        {
            return 0;
        }

        if (!Vault.IsUnlocked && !Unlock(password))
        {
            throw new UserError("Wrong master password.");
        }

        var snippets = _store.LoadSnippets();
        var count = snippets.Count(s => s.Protected);
        var plain = snippets.Select(s => SnippetProtection.Unprotect(s, Vault.Current!)).ToList();
        _store.SaveSnippets(plain);
        _store.DeleteVault();
        Vault.Lock();
        Backups.MarkChanged();
        Sync.NotifyLocalChange();
        Reload();
        return count;
    }

    private Vault UnlockedVault() =>
        Vault.Current ?? throw new UserError(Vault.IsConfigured ? "Unlock your protected snippets first." : "No master password is set yet.");

    // ---- Settings ---------------------------------------------------------

    public void SaveSettings(Settings settings)
    {
        SaveSettingsAndReload(settings);
        Sync.RestartInterval();
    }

    private void SaveSettingsAndReload(Settings settings)
    {
        _store.SaveSettings(settings);
        Reload();
    }

    // ---- Backups ----------------------------------------------------------

    public int RestoreBackup(string zipPath)
    {
        try
        {
            if (_store.HasSnippets)
            {
                Directory.CreateDirectory(BackupDirectory);
                BackupArchive.Create(_store, Path.Combine(BackupDirectory, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip"));
            }

            var count = BackupArchive.Restore(zipPath, _store);
            Vault.Lock();
            Backups.MarkChanged();
            Sync.NotifyLocalChange();
            Reload();
            return count;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new UserError($"Restore failed: {ex.Message}");
        }
    }

    public void ExportSnippets(string path) => File.Copy(_store.SnippetsPath, path, overwrite: true);

    // ---- Breevy -------------------------------------------------------------

    public int ImportBreevy(bool replaceExisting = true)
    {
        var source = BreevyImporter.DefaultDirectory;
        if (!BreevyImporter.IsAvailable(source))
        {
            throw new UserError($"No Breevy data found in {source}.");
        }

        try
        {
            var import = BreevyImporter.Load(source);
            if (replaceExisting)
            {
                _store.BackupSnippets();
            }

            _store.SaveSnippets(import.Snippets);
            // Take Breevy's typing settings but keep everything else configured here.
            _store.SaveSettings(_store.LoadSettings() with
            {
                Delimiters = import.Settings.Delimiters,
                ClipboardThreshold = import.Settings.ClipboardThreshold,
            });
            Backups.MarkChanged();
            Sync.NotifyLocalChange();
            Reload();
            return import.Snippets.Count;
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Xml.XmlException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new UserError($"Breevy import failed: {ex.Message}");
        }
    }

    public bool BreevyAvailable => BreevyImporter.IsAvailable(BreevyImporter.DefaultDirectory);

    /// <summary>Takes a freevy:// link: today only the browser sign-in comes back this way.</summary>
    public async Task HandleLinkAsync(string link)
    {
        try
        {
            var email = await Sync.CompleteBrowserSignInAsync(link);
            Notice?.Invoke($"Signed in as {email}. Syncing your snippets…", false);
        }
        catch (Exception ex) when (ex is FreevyApiException or HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException)
        {
            Notice?.Invoke(ex is FreevyApiException ? ex.Message : "The sign-in could not be finished. Choose \"Sign in with browser\" and try again.", true);
        }

        Changed?.Invoke(Change.Sync);
    }

    public void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.Directory}\"") { UseShellExecute = true });

    // ---- Expansion ----------------------------------------------------------

    private void OnChar(char c)
    {
        if (!_settings.ExpansionEnabled || _busy)
        {
            return;
        }

        var expansion = _engine.Type(c);
        if (expansion is null)
        {
            return;
        }

        // Inject after the hook returns, so the triggering key reaches the app before we delete it.
        _busy = true;
        var target = Native.GetForegroundWindow();
        _ui.Post(async _ =>
        {
            try
            {
                if (expansion.Snippet.Protected)
                {
                    await ExpandProtectedAsync(expansion, target);
                }
                else
                {
                    await TypeAsync(expansion.Deleted, expansion.Text[..^expansion.Suffix.Length], expansion.Suffix, expansion.Snippet.LineBreaks, target);
                }
            }
            catch (Exception ex)
            {
                // Never let a failed expansion take the app down; tell the user instead.
                Notice?.Invoke($"Could not expand: {ex.Message}", true);
            }
            finally
            {
                _busy = false;
            }
        }, null);
    }

    private async Task ExpandProtectedAsync(Expansion expansion, IntPtr target)
    {
        if (!Vault.IsUnlocked)
        {
            if (!Vault.IsConfigured || RequestUnlock is null)
            {
                Notice?.Invoke("This snippet is protected, but no master password is set on this computer.", true);
                return;
            }

            var name = expansion.Deleted[..^expansion.Suffix.Length];
            if (!await RequestUnlock($"“{name}” is protected. Enter your master password to type it."))
            {
                return;
            }

            // The prompt took focus; give it back to the app the abbreviation was typed in.
            Native.SetForegroundWindow(target);
            await Task.Delay(150);
        }

        string text;
        try
        {
            text = SnippetProtection.Reveal(expansion.Snippet, Vault.Current!);
        }
        catch (CryptographicException)
        {
            Notice?.Invoke("This protected snippet could not be decrypted with the current master password.", true);
            return;
        }

        await TypeAsync(expansion.Deleted, text, expansion.Suffix, expansion.Snippet.LineBreaks, target);
    }

    /// <summary>
    /// Types an expansion; text with Breevy-style macros goes through the macro renderer.
    /// <paramref name="suffix"/> is the delimiter the user typed and is always sent as keys.
    /// </summary>
    private async Task TypeAsync(string deleted, string text, string suffix, LineBreakMode? lineBreaks, IntPtr target)
    {
        if (!MacroParser.HasMacros(text))
        {
            _output.Replace(deleted, text, suffix, lineBreaks);
            CountExpansion(deleted, text + suffix);
            return;
        }

        string? Clip() => Clipboard.ContainsText() ? Clipboard.GetText() : null;
        var requests = MacroRenderer.InputsNeeded(text, new MacroContext { FindAbbreviation = FindExpansion });
        IReadOnlyDictionary<string, string> answers = new Dictionary<string, string>();
        if (requests.Count > 0)
        {
            if (RequestInputs is null || await RequestInputs(requests) is not { } given)
            {
                return;
            }

            answers = given;
            // The fill-in window took focus; give it back to the app the abbreviation was typed in.
            Native.SetForegroundWindow(target);
            await Task.Delay(150);
        }

        var plan = MacroRenderer.Render(text, new MacroContext { Clipboard = Clip, FindAbbreviation = FindExpansion, Inputs = answers });
        await _output.RunAsync(deleted, plan, suffix, lineBreaks);
        CountExpansion(deleted, plan.TypedText + suffix);
    }

    /// <summary>%(Abbrev name): the expansion of another snippet. Protected snippets are never pulled in.</summary>
    private string? FindExpansion(string abbreviation) =>
        _snippets.FirstOrDefault(s => !s.Protected && s.Abbreviations.Any(a => a.Equals(abbreviation, StringComparison.OrdinalIgnoreCase)))?.Expansion;

    /// <summary>For the editor: what an expansion would type, with keys and fill-ins shown as markers.</summary>
    public MacroPreviewResult PreviewExpansion(string text) => MacroPreview.Describe(text, FindExpansion);

    private void CountExpansion(string typed, string inserted)
    {
        _stats = _stats.Record(DateOnly.FromDateTime(DateTime.Now), typed.Length, inserted.Length);
        _statsDirty = true;
        Changed?.Invoke(Change.Stats);
    }

    private void FlushStats()
    {
        if (_statsDirty)
        {
            _statsDirty = false;
            _store.SaveStats(_stats);
        }
    }

    // ---- Loading ------------------------------------------------------------

    private void Reload()
    {
        try
        {
            _snippets = _store.LoadSnippets();
            _settings = _store.LoadSettings();
            _engine = new ExpansionEngine(_snippets, _settings.Delimiters);
            _output = new OutputSender(_settings.ClipboardThreshold, _settings.LineBreaks);
            Changed?.Invoke(Change.Snippets);
            Changed?.Invoke(Change.Settings);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // Keep the previous engine; the file may be mid-write or hand-edited into invalid JSON.
            Notice?.Invoke($"Could not load snippets: {ex.Message}", true);
        }
    }

    private void OnDataFileChanged(string? name)
    {
        if (name is DataStore.SnippetsFileName or DataStore.SettingsFileName)
        {
            _ui.Post(_ =>
            {
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }, null);
        }
    }

    private void OnSyncApplied(SyncOutcome outcome)
    {
        if (outcome.VaultReplaced)
        {
            // The master password came from another computer; the old key must not stay usable.
            Vault.Lock();
        }

        Reload();
        if (outcome.ConflictCopies > 0)
        {
            Notice?.Invoke($"{outcome.ConflictCopies} snippet(s) were changed on two computers. The other version is kept in the “{SyncEngine.ConflictFolder}” folder.", false);
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && _settings.LockWhenWindowsLocks)
        {
            _ui.Post(_ => Vault.Lock(), null);
        }
    }

    public void Dispose()
    {
        FlushStats();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hooks.Dispose();
        _watcher.Dispose();
        _reloadTimer.Dispose();
        _statsTimer.Dispose();
        Backups.Dispose();
        Sync.Dispose();
        Updates.Dispose();
        Vault.Dispose();
    }
}
