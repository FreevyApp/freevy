namespace Freevy.Core.Backup;

public sealed record BackupSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Back up at least this often; 0 turns the timed backup off.</summary>
    public int IntervalHours { get; init; } = 24;

    /// <summary>Also back up shortly after snippets change.</summary>
    public bool OnChange { get; init; } = true;

    /// <summary>Newest backups kept per destination; older ones are deleted.</summary>
    public int KeepCount { get; init; } = 10;

    public bool LocalEnabled { get; init; } = true;

    /// <summary>Empty means the "backups" folder inside the data folder.</summary>
    public string LocalDirectory { get; init; } = "";

    /// <summary>A synced folder such as Google Drive for Desktop, OneDrive or Dropbox.</summary>
    public bool FolderEnabled { get; init; }

    public string FolderDirectory { get; init; } = "";

    public SftpSettings Sftp { get; init; } = new();
}

public sealed record SftpSettings
{
    public bool Enabled { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; } = 22;

    public string Username { get; init; } = "";

    /// <summary>Password encrypted for the current Windows user (DPAPI); never plain text.</summary>
    public string ProtectedPassword { get; init; } = "";

    public string PrivateKeyPath { get; init; } = "";

    /// <summary>Private key passphrase encrypted for the current Windows user (DPAPI).</summary>
    public string ProtectedKeyPassphrase { get; init; } = "";

    public string RemoteDirectory { get; init; } = "Freevy-backups";

    /// <summary>SHA-256 host key fingerprint trusted on first connection; connections with another key are refused.</summary>
    public string HostKeyFingerprint { get; init; } = "";
}

/// <summary>Outcome of the last backup run, kept in backup-state.json.</summary>
public sealed record BackupState
{
    public DateTime? LastRunUtc { get; init; }

    public DateTime? LastSuccessUtc { get; init; }

    public DateTime? LastChangeUtc { get; init; }

    public string LastResult { get; init; } = "";
}
