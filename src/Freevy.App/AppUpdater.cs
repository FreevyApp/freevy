using Velopack;

namespace Freevy.App;

/// <summary>
/// Keeps an installed Freevy up to date from freevy.app/releases: checks shortly after start and
/// every few hours. Depending on the settings it downloads a new version in the background and
/// installs it when Freevy exits, or only reports it until the user chooses to install. Does
/// nothing when Freevy runs from a build folder.
/// </summary>
internal sealed class AppUpdater : IDisposable
{
    public const string FeedUrl = "https://freevy.app/releases";

    private readonly UpdateManager _manager = new(FeedUrl);
    // Ticks often and checks only when the chosen interval has passed since the last check.
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)TimeSpan.FromMinutes(15).TotalMilliseconds };
    private readonly System.Windows.Forms.Timer _firstCheck = new() { Interval = (int)TimeSpan.FromSeconds(30).TotalMilliseconds };
    private VelopackAsset? _ready;
    private UpdateInfo? _available;
    private bool _checking;

    /// <summary>Asked before every automatic check; false while the user has turned updates off.</summary>
    public Func<bool> Enabled { get; set; } = () => true;

    /// <summary>Time between automatic checks, from the settings.</summary>
    public Func<TimeSpan> Interval { get; set; } = () => TimeSpan.FromHours(6);

    /// <summary>Stores the time of each completed check, so the interval survives restarts.</summary>
    public Action<DateTime>? Checked { get; set; }

    /// <summary>Whether a check downloads what it finds; false only reports it.</summary>
    public Func<bool> AutoInstall { get; set; } = () => true;

    /// <summary>Raised on the UI thread with the version number once an update is downloaded.</summary>
    public event Action<string>? Ready;

    /// <summary>Raised on the UI thread when a check found a version that was not downloaded.</summary>
    public event Action<string>? Available;

    /// <summary>Raised when a check starts or ends, so the UI can show its state.</summary>
    public event Action? StatusChanged;

    public bool IsInstalled => _manager.IsInstalled;

    public bool IsChecking => _checking;

    public string? ReadyVersion => _ready?.Version.ToString();

    public string? AvailableVersion => _available?.TargetFullRelease.Version.ToString();

    public DateTime? LastCheckedUtc { get; set; }

    private bool Due => Enabled() && (LastCheckedUtc is not { } last || DateTime.UtcNow - last >= Interval());

    public string? LastError { get; private set; }

    public void Start()
    {
        if (!_manager.IsInstalled)
        {
            return;
        }

        _firstCheck.Tick += async (_, _) =>
        {
            _firstCheck.Stop();
            if (Due) await CheckAsync();
        };
        _timer.Tick += async (_, _) =>
        {
            if (Due) await CheckAsync();
        };
        _firstCheck.Start();
        _timer.Start();
    }

    /// <param name="download">Download what is found; null follows <see cref="AutoInstall"/>.</param>
    public async Task CheckAsync(bool? download = null)
    {
        if (_checking || _ready is not null || !_manager.IsInstalled)
        {
            return;
        }

        _checking = true;
        StatusChanged?.Invoke();
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            LastCheckedUtc = DateTime.UtcNow;
            Checked?.Invoke(LastCheckedUtc.Value);
            LastError = null;
            _available = update;
            if (update is null)
            {
                return;
            }

            if (!(download ?? AutoInstall()))
            {
                Available?.Invoke(update.TargetFullRelease.Version.ToString());
                return;
            }

            await _manager.DownloadUpdatesAsync(update);
            _ready = update.TargetFullRelease;
            Ready?.Invoke(_ready.Version.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            // Offline or the feed is unreachable: try again at the next interval.
            LastError = L.T("Could not reach freevy.app. Check your connection and try again.");
        }
        finally
        {
            _checking = false;
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Downloads the newest version if needed, installs it and starts Freevy again.</summary>
    public async Task InstallNowAsync()
    {
        if (_ready is null)
        {
            await CheckAsync(download: true);
        }

        RestartToUpdate();
    }

    /// <summary>Installs the downloaded update now and starts Freevy again.</summary>
    public void RestartToUpdate()
    {
        if (_ready is not null)
        {
            _manager.ApplyUpdatesAndRestart(_ready);
        }
    }

    /// <summary>Called on exit: a downloaded update installs quietly once this process has ended.</summary>
    public void ApplyOnExit()
    {
        if (_ready is not null)
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _firstCheck.Dispose();
    }
}
