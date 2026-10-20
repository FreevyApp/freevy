using System.Diagnostics;
using System.Net;
using Freevy.Core;
using Freevy.Core.Security;
using Freevy.Core.Sync;

namespace Freevy.App;

/// <summary>
/// Signs this computer in to a Freevy account and keeps snippets in sync: on a timer, a few
/// seconds after local edits, and on demand. Network work runs off the UI thread; results are
/// applied on the UI thread only if nothing changed locally in the meantime.
/// </summary>
internal sealed class SyncManager : IDisposable
{
    private readonly DataStore _store;
    private readonly Func<Settings> _settings;
    private readonly Action<Settings> _saveSettings;
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _interval = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 5000 };
    private long _localGeneration;
    private bool _running;
    private bool _again;
    private BrowserSignIn? _browserSignIn;

    public SyncManager(DataStore store, Func<Settings> settings, Action<Settings> saveSettings, SynchronizationContext ui)
    {
        _store = store;
        _settings = settings;
        _saveSettings = saveSettings;
        _ui = ui;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SyncNow();
        };
        _interval.Tick += (_, _) => SyncNow();
        RestartInterval();
    }

    /// <summary>Raised on the UI thread when status changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised on the UI thread after a sync changed local data.</summary>
    public event Action<SyncOutcome>? Applied;

    /// <summary>Raised on the UI thread when the server signed this computer out.</summary>
    public event Action<string>? SignedOut;

    public bool IsSignedIn => _settings().Sync.IsSignedIn;

    public bool IsRunning => _running;

    public DateTime? LastSyncUtc => _store.LoadSyncState().LastSyncUtc;

    public string? LastError { get; private set; }

    /// <summary>Call after every local write of snippets or the vault.</summary>
    public void NotifyLocalChange()
    {
        Interlocked.Increment(ref _localGeneration);
        if (IsSignedIn)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    public void RestartInterval()
    {
        _interval.Stop();
        _interval.Interval = Math.Max(1, _settings().Sync.IntervalMinutes) * 60_000;
        if (IsSignedIn)
        {
            _interval.Start();
        }
    }

    /// <summary>Derives keys from the password, signs in, stores the token and data key with DPAPI, then syncs.</summary>
    public async Task SignInAsync(Uri server, string email, string password, string deviceName, CancellationToken cancellationToken)
    {
        using var client = new FreevyApiClient(server);
        var kdf = await client.PreloginAsync(email, cancellationToken);
        var masterKey = await Task.Run(() => AccountCrypto.DeriveMasterKey(password, kdf), cancellationToken);
        var login = await client.LoginAsync(email, AccountCrypto.DeriveAuthHash(masterKey, password), deviceName, cancellationToken);

        byte[] dataKey;
        try
        {
            dataKey = AccountCrypto.UnwrapDataKey(masterKey, login.WrappedDataKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new FreevyApiException("Signed in, but the account key could not be opened. Reset your password on the website.", System.Net.HttpStatusCode.OK);
        }

        StoreSession(server, email, deviceName, login.Token, dataKey);
    }

    /// <summary>
    /// Starts a browser sign-in: opens the website with a one-time public key. The website sends
    /// the result back through a freevy:// link, which <see cref="CompleteBrowserSignInAsync"/> takes.
    /// </summary>
    public BrowserSignIn BeginBrowserSignIn(Uri server, string deviceName, bool openBrowser = true)
    {
        _browserSignIn?.Dispose();
        _browserSignIn = BrowserSignIn.Start(server, deviceName);
        if (openBrowser)
        {
            Process.Start(new ProcessStartInfo(_browserSignIn.BrowserUrl.AbsoluteUri) { UseShellExecute = true });
        }

        return _browserSignIn;
    }

    public void CancelBrowserSignIn()
    {
        _browserSignIn?.Dispose();
        _browserSignIn = null;
    }

    /// <summary>Finishes a browser sign-in from its freevy:// link and returns the account's email.</summary>
    public async Task<string> CompleteBrowserSignInAsync(string link)
    {
        var parsed = BrowserSignIn.ParseLink(link)
            ?? throw new FreevyApiException("That is not a Freevy sign-in link.", HttpStatusCode.BadRequest);
        var pending = _browserSignIn;
        if (pending is null || !string.Equals(pending.State, parsed.State, StringComparison.Ordinal))
        {
            throw new FreevyApiException("This sign-in was not started in Freevy on this computer. Choose \"Sign in with browser\" in Freevy and try again.", HttpStatusCode.BadRequest);
        }

        _browserSignIn = null;
        using (pending)
        {
            if (pending.IsExpired(DateTime.UtcNow))
            {
                throw new FreevyApiException("The sign-in took too long. Choose \"Sign in with browser\" again.", HttpStatusCode.BadRequest);
            }

            using var client = new FreevyApiClient(pending.Server);
            var result = await client.RedeemLinkAsync(pending, parsed.Code, CancellationToken.None);
            StoreSession(pending.Server, result.Email, pending.DeviceName, result.Token, result.DataKey);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(result.DataKey);
            return result.Email;
        }
    }

    public bool IsBrowserSignInPending => _browserSignIn is { } pending && !pending.IsExpired(DateTime.UtcNow);

    /// <summary>Keeps the token and data key (DPAPI), starts from a clean sync state and syncs.</summary>
    private void StoreSession(Uri server, string email, string deviceName, string token, byte[] dataKey)
    {
        _store.DeleteSyncState();
        var settings = _settings();
        _saveSettings(settings with
        {
            Sync = settings.Sync with
            {
                ServerUrl = server.GetLeftPart(UriPartial.Authority),
                Email = email.Trim().ToLowerInvariant(),
                DeviceName = deviceName,
                ProtectedToken = Secrets.Protect(token),
                ProtectedDataKey = Secrets.Protect(Convert.ToBase64String(dataKey)),
            },
        });
        LastError = null;
        RestartInterval();
        SyncNow();
    }

    /// <summary>Server-side history, decrypted on this PC. Versions that are not snippets are left out.</summary>
    public async Task<IReadOnlyList<(HistoryVersion Version, Snippet Snippet)>> GetHistoryAsync()
    {
        var sync = _settings().Sync;
        var token = Secrets.Unprotect(sync.ProtectedToken);
        var dataKey = Secrets.Unprotect(sync.ProtectedDataKey);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(dataKey))
        {
            throw new FreevyApiException("Sign in to see the server history.", System.Net.HttpStatusCode.Unauthorized);
        }

        using var client = new FreevyApiClient(new Uri(sync.ServerUrl), token);
        var versions = await client.HistoryAsync(CancellationToken.None);
        var itemKey = AccountCrypto.ItemKey(Convert.FromBase64String(dataKey));
        var result = new List<(HistoryVersion, Snippet)>();
        foreach (var version in versions)
        {
            try
            {
                if (SyncItems.ToSnippet(version.Id, AccountCrypto.OpenItem(itemKey, version.Id, version.Ciphertext)) is { } snippet)
                {
                    result.Add((version, snippet));
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Sealed with another key (for example before an erase); cannot be shown.
            }
        }

        return result;
    }

    public async Task SignOutAsync()
    {
        var sync = _settings().Sync;
        if (Secrets.Unprotect(sync.ProtectedToken) is { Length: > 0 } token)
        {
            try
            {
                using var client = new FreevyApiClient(new Uri(sync.ServerUrl), token);
                await client.LogoutAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or FreevyApiException or SignedOutException or TaskCanceledException)
            {
                // Already signed out or offline: forgetting the token locally is what matters.
            }
        }

        ForgetAccount();
    }

    public void SyncNow()
    {
        if (!IsSignedIn)
        {
            return;
        }

        if (_running)
        {
            _again = true;
            return;
        }

        var sync = _settings().Sync;
        var token = Secrets.Unprotect(sync.ProtectedToken);
        var dataKey = Secrets.Unprotect(sync.ProtectedDataKey);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(dataKey))
        {
            LastError = "The saved sign-in cannot be read on this computer. Sign in again.";
            StatusChanged?.Invoke();
            return;
        }

        _running = true;
        _again = false;
        StatusChanged?.Invoke();

        var generation = Interlocked.Read(ref _localGeneration);
        var snippets = _store.LoadSnippets();
        var vault = _store.LoadVault();
        var state = _store.LoadSyncState();
        var server = new Uri(sync.ServerUrl);

        Task.Run(async () =>
        {
            using var client = new FreevyApiClient(server, token);
            var engine = new SyncEngine(client, AccountCrypto.ItemKey(Convert.FromBase64String(dataKey)));
            return await engine.RunAsync(snippets, vault, state, CancellationToken.None);
        }).ContinueWith(task => _ui.Post(_ => Finish(task, generation, snippets), null), TaskScheduler.Default);
    }

    private void Finish(Task<SyncOutcome> task, long generation, IReadOnlyList<Snippet> before)
    {
        _running = false;
        if (task.IsFaulted)
        {
            var error = task.Exception!.GetBaseException();
            if (error is SignedOutException)
            {
                ForgetAccount();
                SignedOut?.Invoke(error.Message);
            }
            else
            {
                LastError = error is HttpRequestException or TaskCanceledException
                    ? "Could not reach the Freevy server. Will try again."
                    : error.Message;
            }

            StatusChanged?.Invoke();
            return;
        }

        if (Interlocked.Read(ref _localGeneration) != generation)
        {
            // Something was edited while syncing: drop this result and sync again from the new state.
            _running = false;
            SyncNow();
            return;
        }

        var outcome = task.Result;
        var snippetsChanged = !outcome.Snippets.SequenceEqual(before);
        if (snippetsChanged)
        {
            _store.SaveSnippets(outcome.Snippets);
        }

        if (outcome.VaultReplaced && outcome.Vault is not null)
        {
            _store.SaveVault(outcome.Vault);
        }

        _store.SaveSyncState(outcome.State);
        LastError = outcome.VaultConflict
            ? "This computer and your account use different master passwords. Protected snippets from other computers will not open here."
            : null;

        if (snippetsChanged || outcome.VaultReplaced)
        {
            Applied?.Invoke(outcome);
        }

        StatusChanged?.Invoke();
        if (_again)
        {
            SyncNow();
        }
    }

    private void ForgetAccount()
    {
        var settings = _settings();
        _saveSettings(settings with { Sync = settings.Sync with { ProtectedToken = "", ProtectedDataKey = "" } });
        _store.DeleteSyncState();
        _interval.Stop();
        _debounce.Stop();
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _interval.Dispose();
        _debounce.Dispose();
        _browserSignIn?.Dispose();
    }
}
