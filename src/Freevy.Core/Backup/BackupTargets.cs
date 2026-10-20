using Renci.SshNet;
using Renci.SshNet.Common;

namespace Freevy.Core.Backup;

public interface IBackupTarget
{
    string Name { get; }

    /// <summary>Uploads the archive under its own file name, then deletes all but the newest <paramref name="keep"/> backups.</summary>
    void Upload(string zipPath, int keep);
}

/// <summary>A local or synced folder (Google Drive for Desktop, OneDrive, Dropbox, a NAS share).</summary>
public sealed class FolderBackupTarget(string name, string directory) : IBackupTarget
{
    public string Name { get; } = name;

    public void Upload(string zipPath, int keep)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(zipPath));
        var temp = destination + ".partial";
        File.Copy(zipPath, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);

        foreach (var expired in BackupArchive.SelectExpired(Directory.EnumerateFiles(directory).Select(Path.GetFileName)!, keep))
        {
            File.Delete(Path.Combine(directory, expired));
        }
    }
}

/// <param name="Password">Plain password, already decrypted by the caller; null when using a key.</param>
public sealed record SftpConnection(
    string Host,
    int Port,
    string Username,
    string? Password,
    string? PrivateKeyPath,
    string? KeyPassphrase,
    string RemoteDirectory,
    string TrustedFingerprint);

public sealed class SftpBackupTarget(SftpConnection connection) : IBackupTarget
{
    public string Name => $"SFTP {connection.Host}";

    /// <summary>
    /// Connects, accepting any host key, and returns the server's SHA-256 fingerprint so the
    /// user can confirm it. Also checks that the remote folder can be created.
    /// </summary>
    public static string Probe(SftpConnection connection)
    {
        var fingerprint = "";
        using var client = CreateClient(connection);
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint = e.FingerPrintSHA256;
            e.CanTrust = connection.TrustedFingerprint.Length == 0 || e.FingerPrintSHA256 == connection.TrustedFingerprint;
        };
        client.Connect();
        EnsureDirectory(client, connection.RemoteDirectory);
        client.Disconnect();
        return fingerprint;
    }

    public void Upload(string zipPath, int keep)
    {
        if (connection.TrustedFingerprint.Length == 0)
        {
            throw new InvalidOperationException("The SFTP server's host key is not trusted yet. Use \"Test connection\" in Settings first.");
        }

        using var client = CreateClient(connection);
        var rejected = false;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = e.FingerPrintSHA256 == connection.TrustedFingerprint;
            rejected = !e.CanTrust;
        };
        try
        {
            client.Connect();
        }
        catch (SshConnectionException ex) when (rejected)
        {
            throw new InvalidOperationException("The SFTP server presented a different host key than the trusted one. Backup refused.", ex);
        }

        var directory = EnsureDirectory(client, connection.RemoteDirectory);
        var name = Path.GetFileName(zipPath);
        var temp = Combine(directory, name + ".partial");
        using (var stream = File.OpenRead(zipPath))
        {
            client.UploadFile(stream, temp, canOverride: true);
        }

        var final = Combine(directory, name);
        if (client.Exists(final))
        {
            client.DeleteFile(final);
        }

        client.RenameFile(temp, final);

        var names = client.ListDirectory(directory).Where(f => f.IsRegularFile).Select(f => f.Name);
        foreach (var expired in BackupArchive.SelectExpired(names, keep).ToList())
        {
            client.DeleteFile(Combine(directory, expired));
        }

        client.Disconnect();
    }

    private static SftpClient CreateClient(SftpConnection connection)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(connection.PrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(connection.KeyPassphrase)
                ? new PrivateKeyFile(connection.PrivateKeyPath)
                : new PrivateKeyFile(connection.PrivateKeyPath, connection.KeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(connection.Username, key));
        }

        if (!string.IsNullOrEmpty(connection.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(connection.Username, connection.Password));
        }

        if (methods.Count == 0)
        {
            throw new InvalidOperationException("Enter an SFTP password or choose a private key file.");
        }

        var info = new ConnectionInfo(connection.Host, connection.Port, connection.Username, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        return new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>Creates each missing segment of the remote path and returns the absolute directory.</summary>
    private static string EnsureDirectory(SftpClient client, string remoteDirectory)
    {
        var path = remoteDirectory.Replace('\\', '/').Trim();
        var current = path.StartsWith('/') ? "" : client.WorkingDirectory.TrimEnd('/');
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }

        return current.Length == 0 ? "/" : current;
    }

    private static string Combine(string directory, string name) => directory.TrimEnd('/') + "/" + name;
}
