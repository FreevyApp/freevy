using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace Freevy.Core.Sync;

/// <summary>Argon2id parameters as the server stores and returns them.</summary>
public sealed record KdfParameters(string Algorithm, string Salt, int Memory, int Iterations, int Parallelism);

/// <summary>
/// The account-side cryptography shared with the website. Must stay byte-identical to
/// freevy-web/resources/js/crypto.js; the contract is freevy-web/docs/crypto.md and the
/// cross-implementation vectors in the tests.
/// </summary>
public static class AccountCrypto
{
    private const string Prefix = "v1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ItemPadding = 256;
    private static readonly byte[] WrapInfo = Encoding.UTF8.GetBytes("freevy-wrap-v1");
    private static readonly byte[] ItemInfo = Encoding.UTF8.GetBytes("freevy-items-v1");

    // Plain UTF-8 like JSON.stringify, so both clients produce the same sizes.
    private static readonly JsonSerializerOptions ItemJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Argon2id(password, salt) to a 32-byte master key. Slow on purpose (about a second).</summary>
    public static byte[] DeriveMasterKey(string password, KdfParameters kdf)
    {
        if (!string.Equals(kdf.Algorithm, "argon2id", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unsupported key derivation '{kdf.Algorithm}'.");
        }

        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = Convert.FromBase64String(kdf.Salt),
            MemorySize = kdf.Memory,
            Iterations = kdf.Iterations,
            DegreeOfParallelism = kdf.Parallelism,
        };
        return argon.GetBytes(32);
    }

    /// <summary>PBKDF2-HMAC-SHA256(masterKey, salt = password, 1 round), base64; what the server checks.</summary>
    public static string DeriveAuthHash(byte[] masterKey, string password) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(masterKey, Encoding.UTF8.GetBytes(password), 1, HashAlgorithmName.SHA256, 32));

    public static byte[] UnwrapDataKey(byte[] masterKey, string wrapped) =>
        Open(HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, [], WrapInfo), wrapped, []);

    public static byte[] ItemKey(byte[] dataKey) => HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, [], ItemInfo);

    /// <summary>Seals item JSON, padded to 256-byte blocks, bound to the item id as associated data.</summary>
    public static string SealItem(byte[] itemKey, Guid id, JsonElement value)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, ItemJson));
        var padded = new byte[(json.Length + 1 + ItemPadding - 1) / ItemPadding * ItemPadding];
        Array.Fill(padded, (byte)' ');
        json.CopyTo(padded, 0);
        return Seal(itemKey, padded, AssociatedData(id));
    }

    /// <exception cref="CryptographicException">Wrong key, wrong id, or altered ciphertext.</exception>
    public static JsonElement OpenItem(byte[] itemKey, Guid id, string sealedText)
    {
        var plain = Open(itemKey, sealedText, AssociatedData(id));
        using var document = JsonDocument.Parse(plain);
        return document.RootElement.Clone();
    }

    private static byte[] AssociatedData(Guid id) => Encoding.UTF8.GetBytes(id.ToString("D").ToLowerInvariant());

    /// <summary>"v1." + base64(nonce | ciphertext | tag), the order WebCrypto produces.</summary>
    private static string Seal(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var payload = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = payload.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, payload.AsSpan(NonceSize, plaintext.Length), payload.AsSpan(NonceSize + plaintext.Length), associatedData);
        return Prefix + Convert.ToBase64String(payload);
    }

    private static byte[] Open(byte[] key, string sealedText, byte[] associatedData)
    {
        if (!sealedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown sealed value format.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(sealedText[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Sealed value is not valid base64.", ex);
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Sealed value is too short.");
        }

        var length = payload.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize, length), payload.AsSpan(NonceSize + length), plain, associatedData);
        return plain;
    }
}
