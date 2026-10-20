using System.Security.Cryptography;
using System.Text;

namespace Freevy.Core.Security;

/// <summary>Stored in vault.json: what is needed to re-derive and verify the master key.</summary>
public sealed record VaultHeader(string Salt, int Iterations, string Verifier);

/// <summary>
/// Master-password key in memory. Key derivation is PBKDF2-HMAC-SHA256, encryption is
/// AES-256-GCM with a fresh 12-byte nonce per value. Payload layout: base64(nonce | tag | ciphertext).
/// </summary>
public sealed class Vault : IDisposable
{
    public const int DefaultIterations = 600_000;
    public const int MinimumPasswordLength = 8;
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    // Keeps the pre-rename value: vaults created by OzanExtender must still unlock.
    private const string VerifierText = "OzanExtender vault v1";

    private readonly byte[] _key;
    private bool _disposed;

    private Vault(byte[] key) => _key = key;

    public static (VaultHeader Header, Vault Vault) Create(string password, int iterations = DefaultIterations)
    {
        if (password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException($"The master password must be at least {MinimumPasswordLength} characters.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var vault = new Vault(DeriveKey(password, salt, iterations));
        var header = new VaultHeader(Convert.ToBase64String(salt), iterations, vault.Encrypt(VerifierText));
        return (header, vault);
    }

    /// <summary>Returns the unlocked vault, or null when the password is wrong.</summary>
    public static Vault? TryUnlock(VaultHeader header, string password)
    {
        var vault = new Vault(DeriveKey(password, Convert.FromBase64String(header.Salt), header.Iterations));
        try
        {
            if (vault.Decrypt(header.Verifier) == VerifierText)
            {
                return vault;
            }
        }
        catch (CryptographicException)
        {
            // Wrong key: GCM authentication fails.
        }

        vault.Dispose();
        return null;
    }

    public string Encrypt(string plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var payload = new byte[NonceSize + TagSize + data.Length];
        var nonce = payload.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize));
        CryptographicOperations.ZeroMemory(data);
        return Convert.ToBase64String(payload);
    }

    /// <exception cref="CryptographicException">The payload was not sealed with this key or was altered.</exception>
    public string Decrypt(string payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Encrypted value is not valid base64.", ex);
        }

        if (bytes.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Encrypted value is too short.");
        }

        var plain = new byte[bytes.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(bytes.AsSpan(0, NonceSize), bytes.AsSpan(NonceSize + TagSize), bytes.AsSpan(NonceSize, TagSize), plain);
        var text = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return text;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
}
