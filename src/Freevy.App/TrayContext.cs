using Freevy.Core;

namespace Freevy.App;

/// <summary>Tray icon, its menu, the main window and single-instance activation.</summary>
internal sealed class TrayContext : ApplicationContext
{
    public const string ShowWindowEventName = @"Local\Freevy.ShowWindow";
    private const string AppName = "Freevy";

    private readonly AppCore _core;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _lockItem;
    private readonly EventWaitHandle _showWindowEvent = new(false, EventResetMode.AutoReset, ShowWindowEventName);
    private readonly RegisteredWaitHandle _showWindowWait;
    private readonly SynchronizationContext _ui;
    private readonly LinkChannel _links;
    private WebWindow? _window;
    private Task<bool>? _openPrompt;

    public TrayContext(DataStore store, bool startHidden, string? link = null)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _core = new AppCore(store, _ui) { RequestUnlock = AskForMasterPassword };

        _enabledItem = new ToolStripMenuItem("Expansion enabled", null, (_, _) =>
            _core.SaveSettings(_core.Settings with { ExpansionEnabled = !_core.Settings.ExpansionEnabled }));
        _lockItem = new ToolStripMenuItem("Unlock protected snippets…", null, async (_, _) =>
        {
            if (_core.Vault.IsUnlocked)
            {
                _core.Lock();
            }
            else if (_core.Vault.IsConfigured)
            {
                await AskForMasterPassword("Unlock your protected snippets.");
            }
        });

        var updateItem = new ToolStripMenuItem("Restart to update", null, (_, _) => _core.Updates.RestartToUpdate()) { Visible = false };
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open Freevy", null, (_, _) => ShowWindow()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        menu.Items.AddRange(
        [
            openItem,
            updateItem,
            new ToolStripSeparator(),
            _enabledItem,
            _lockItem,
            new ToolStripMenuItem("Back up now", null, (_, _) => _core.Backups.RunNow()),
            new ToolStripMenuItem("Sync now", null, (_, _) => _core.Sync.SyncNow()),
            new ToolStripMenuItem("Open data folder", null, (_, _) => _core.OpenDataFolder()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        menu.Opening += (_, _) => UpdateMenu();

        _tray = new NotifyIcon { Icon = AppIcon.Get(), Text = AppName, ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
        };

        _core.Changed += _ => UpdateTrayText();
        _core.Notice += (message, problem) =>
        {
            // The window shows its own toast; the balloon covers the case where it is hidden.
            if (_window is not { Visible: true })
            {
                _tray.ShowBalloonTip(5000, AppName, message, problem ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
        };
        UpdateTrayText();

        // freevy:// links (browser sign-in) arrive from the process Windows starts for them.
        _links = new LinkChannel(_ui, OpenLink);

        _core.Updates.Ready += version =>
        {
            updateItem.Text = $"Restart to update to {version}";
            updateItem.Visible = true;
            _core.Notify($"Freevy {version} is ready. It installs the next time Freevy starts, or restart it from the tray menu.");
            _core.RaiseChanged(Change.Settings);
        };
        if (!WebWindow.Offscreen)
        {
            _core.Updates.Start();
        }

        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent, (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!startHidden)
        {
            ShowWindow();
        }

        if (link is not null)
        {
            _ui.Post(_ => OpenLink(link), null);
        }
    }

    private void OpenLink(string link)
    {
        ShowWindow();
        _ = _core.HandleLinkAsync(link);
    }

    private void ShowWindow()
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new WebWindow(_core);
        }

        _window.ShowAndActivate();
    }

    /// <summary>One prompt at a time; a second request while it is open waits for the same answer.</summary>
    private Task<bool> AskForMasterPassword(string reason)
    {
        if (_openPrompt is { IsCompleted: false })
        {
            return _openPrompt;
        }

        _openPrompt = PromptWindow.AskAsync(_core, reason);
        return _openPrompt;
    }

    private void UpdateMenu()
    {
        _enabledItem.Checked = _core.Settings.ExpansionEnabled;
        _lockItem.Enabled = _core.Vault.IsConfigured;
        _lockItem.Text = _core.Vault.IsUnlocked
            ? $"Lock protected snippets (unlocked until {_core.Vault.LocksAtLocal:HH:mm})"
            : "Unlock protected snippets…";
    }

    private void UpdateTrayText()
    {
        var state = _core.Settings.ExpansionEnabled ? $"{_core.AbbreviationCount} abbreviations" : "paused";
        var vault = _core.Vault.IsUnlocked ? ", unlocked" : "";
        var text = $"{AppName}: {state}{vault}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    protected override void ExitThreadCore()
    {
        _links.Dispose();
        _core.Updates.ApplyOnExit();
        _showWindowWait.Unregister(null);
        _showWindowEvent.Dispose();
        _window?.Dispose();
        _core.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
