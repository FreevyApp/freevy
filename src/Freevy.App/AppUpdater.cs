using Velopack;

namespace Freevy.App;

/// <summary>
/// Keeps an installed Freevy up to date from freevy.app/releases: checks shortly after start and
/// every few hours, downloads a new version in the background, and installs it when Freevy exits
/// or when the user chooses to restart. Does nothing when Freevy runs from a build folder.
/// </summary>
internal sealed class AppUpdater : IDisposable
{
    public const string FeedUrl = "https://freevy.app/releases";

    private readonly UpdateManager _manager = new(FeedUrl);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds };
    private readonly System.Windows.Forms.Timer _firstCheck = new() { Interval = (int)TimeSpan.FromSeconds(30).TotalMilliseconds };
    private VelopackAsset? _ready;
    private bool _checking;

    /// <summary>Asked before every automatic check; false while the user has turned updates off.</summary>
    public Func<bool> Enabled { get; set; } = () => true;

    /// <summary>Raised on the UI thread with the version number once an update is downloaded.</summary>
    public event Action<string>? Ready;

    public bool IsInstalled => _manager.IsInstalled;

    public string? ReadyVersion => _ready?.Version.ToString();

    public void Start()
    {
        if (!_manager.IsInstalled)
        {
            return;
        }

        _firstCheck.Tick += async (_, _) =>
        {
            _firstCheck.Stop();
            if (Enabled()) await CheckAsync();
        };
        _timer.Tick += async (_, _) =>
        {
            if (Enabled()) await CheckAsync();
        };
        _firstCheck.Start();
        _timer.Start();
    }

    public async Task CheckAsync()
    {
        if (_checking || _ready is not null || !_manager.IsInstalled)
        {
            return;
        }

        _checking = true;
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return;
            }

            await _manager.DownloadUpdatesAsync(update);
            _ready = update.TargetFullRelease;
            Ready?.Invoke(_ready.Version.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            // Offline or the feed is unreachable: try again at the next interval.
        }
        finally
        {
            _checking = false;
        }
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
