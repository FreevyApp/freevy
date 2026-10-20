using Freevy.Core;
using Freevy.Core.Security;

namespace Freevy.App;

/// <summary>Holds the unlocked master key for a limited time and wipes it on lock.</summary>
internal sealed class VaultSession : IDisposable
{
    private readonly DataStore _store;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private Vault? _vault;
    private DateTime _lockAtUtc;

    public VaultSession(DataStore store)
    {
        _store = store;
        _timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _lockAtUtc)
            {
                Lock();
            }
        };
    }

    public event Action? Changed;

    public bool IsConfigured => _store.LoadVault() is not null;

    public bool IsUnlocked => _vault is not null;

    public DateTime? LocksAtLocal => _vault is null ? null : _lockAtUtc.ToLocalTime();

    /// <summary>The unlocked vault; only valid until the next lock.</summary>
    public Vault? Current => _vault;

    public bool TryUnlock(string password, int minutes)
    {
        var header = _store.LoadVault();
        var vault = header is null ? null : Vault.TryUnlock(header, password);
        if (vault is null)
        {
            return false;
        }

        Adopt(vault, minutes);
        return true;
    }

    /// <summary>Takes ownership of an already unlocked vault, e.g. right after setting a new master password.</summary>
    public void Adopt(Vault vault, int minutes)
    {
        _vault?.Dispose();
        _vault = vault;
        _lockAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, minutes));
        _timer.Start();
        Changed?.Invoke();
    }

    public void Lock()
    {
        _timer.Stop();
        if (_vault is null)
        {
            return;
        }

        _vault.Dispose();
        _vault = null;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _vault?.Dispose();
        _vault = null;
        _timer.Dispose();
    }
}
