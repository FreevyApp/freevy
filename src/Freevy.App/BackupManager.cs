using Freevy.Core;
using Freevy.Core.Backup;

namespace Freevy.App;

/// <summary>Checks once a minute whether a backup is due and runs it off the UI thread.</summary>
internal sealed class BackupManager : IDisposable
{
    private readonly DataStore _store;
    private readonly Func<Settings> _settings;
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private int _running;

    public BackupManager(DataStore store, Func<Settings> settings, SynchronizationContext ui)
    {
        _store = store;
        _settings = settings;
        _ui = ui;
        _timer.Tick += (_, _) => RunIfDue();
        _timer.Start();
    }

    /// <summary>Raised on the UI thread after every run.</summary>
    public event Action<BackupResult>? Completed;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public BackupState State => _store.LoadBackupState();

    public void MarkChanged() => _store.SaveBackupState(_store.LoadBackupState() with { LastChangeUtc = DateTime.UtcNow });

    public void RunIfDue()
    {
        if (BackupSchedule.IsDue(_settings().Backup, _store.LoadBackupState(), DateTime.UtcNow))
        {
            RunNow();
        }
    }

    public void RunNow()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        var settings = _settings().Backup;
        Task.Run(() =>
        {
            BackupResult result;
            try
            {
                result = BackupRunner.Run(_store, BuildTargets(settings), settings.KeepCount);
            }
            catch (Exception ex)
            {
                result = new BackupResult(false, ex.Message);
            }

            var now = DateTime.UtcNow;
            var state = _store.LoadBackupState();
            _store.SaveBackupState(state with
            {
                LastRunUtc = now,
                LastSuccessUtc = result.Success ? now : state.LastSuccessUtc,
                LastResult = result.Summary,
            });
            Volatile.Write(ref _running, 0);
            _ui.Post(_ => Completed?.Invoke(result), null);
        });
    }

    public IReadOnlyList<IBackupTarget> BuildTargets(BackupSettings settings)
    {
        var targets = new List<IBackupTarget>();
        if (settings.LocalEnabled)
        {
            var local = settings.LocalDirectory.Length > 0 ? settings.LocalDirectory : _store.DefaultBackupDirectory;
            targets.Add(new FolderBackupTarget("Local folder", local));
        }

        if (settings.FolderEnabled && settings.FolderDirectory.Length > 0)
        {
            targets.Add(new FolderBackupTarget("Synced folder", settings.FolderDirectory));
        }

        if (settings.Sftp.Enabled)
        {
            targets.Add(new SftpBackupTarget(ToConnection(settings.Sftp)));
        }

        return targets;
    }

    public static SftpConnection ToConnection(SftpSettings sftp)
    {
        var password = Secrets.Unprotect(sftp.ProtectedPassword)
            ?? throw new InvalidOperationException("The saved SFTP password cannot be decrypted on this PC. Enter it again in Settings.");
        var passphrase = Secrets.Unprotect(sftp.ProtectedKeyPassphrase)
            ?? throw new InvalidOperationException("The saved key passphrase cannot be decrypted on this PC. Enter it again in Settings.");
        return new SftpConnection(sftp.Host, sftp.Port, sftp.Username, password, sftp.PrivateKeyPath, passphrase,
            sftp.RemoteDirectory, sftp.HostKeyFingerprint);
    }

    public void Dispose() => _timer.Dispose();
}
