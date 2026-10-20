using System.Security.Cryptography;
using System.Text;

namespace Freevy.Core.Sync;

/// <summary>The device token and data key a finished browser sign-in hands to the app.</summary>
public sealed record BrowserSignInResult(string Email, string Token, byte[] DataKey);

/// <summary>
/// One pending "sign in with browser" (freevy-web/docs/crypto.md, "Browser sign-in"). The app keeps a
/// one-time ECDH P-256 key pair, a PKCE verifier and a state value in memory; the website seals the
/// data key to the public key, and the freevy:// link brings back a one-time code. Only this object
/// can redeem the code (verifier) and open the key (private key), so a stolen link is useless.
/// Must stay byte-identical to freevy-web/resources/js/crypto.js (sealForDevice).
/// </summary>
public sealed class BrowserSignIn : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly byte[] LinkInfo = Encoding.UTF8.GetBytes("freevy-link-v1");

    private readonly ECDiffieHellman _key;
    private readonly string _verifier;

    private BrowserSignIn(ECDiffieHellman key, string verifier, string state, Uri server, string deviceName, DateTime startedUtc)
    {
        _key = key;
        _verifier = verifier;
        State = state;
        Server = server;
        DeviceName = deviceName;
        StartedUtc = startedUtc;
        DeviceKey = Base64Url(key.ExportSubjectPublicKeyInfo());
        Challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    public Uri Server { get; }

    public string DeviceName { get; }

    public string State { get; }

    public DateTime StartedUtc { get; }

    /// <summary>base64url SPKI of the one-time public key.</summary>
    public string DeviceKey { get; }

    public string Challenge { get; }

    public string Verifier => _verifier;

    /// <summary>The digits the website shows next to the device name, so the user can compare them.</summary>
    public string Fingerprint => FingerprintOf(DeviceKey);

    public bool IsExpired(DateTime nowUtc) => nowUtc - StartedUtc > Lifetime;

    public static BrowserSignIn Start(Uri server, string deviceName) =>
        new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), Base64Url(RandomNumberGenerator.GetBytes(32)),
            Base64Url(RandomNumberGenerator.GetBytes(18)), server, deviceName, DateTime.UtcNow);

    /// <summary>For tests: a sign-in around a known private key and verifier.</summary>
    public static BrowserSignIn FromKey(byte[] pkcs8PrivateKey, string verifier, Uri server)
    {
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        return new BrowserSignIn(key, verifier, "test-state-0000000000", server, "Test PC", DateTime.UtcNow);
    }

    /// <summary>The website page that asks for the password and sends the result back.</summary>
    public Uri BrowserUrl => new(Server, "/link?" + string.Join('&',
        $"device={Uri.EscapeDataString(DeviceName)}",
        $"key={DeviceKey}",
        $"challenge={Challenge}",
        $"state={State}"));

    /// <summary>Reads code and state from freevy://link?code=…&amp;state=…; null when it is not such a link.</summary>
    public static (string Code, string State)? ParseLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("freevy", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("link", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? code = null, state = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (parts[0] == "code") code = value;
            if (parts[0] == "state") state = value;
        }

        return string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) ? null : (code, state);
    }

    /// <summary>Opens the data key the browser sealed to this sign-in's public key.</summary>
    public byte[] OpenDataKey(string browserKey, string sealedKey)
    {
        using var browser = ECDiffieHellman.Create();
        browser.ImportSubjectPublicKeyInfo(FromBase64Url(browserKey), out _);
        var shared = _key.DeriveRawSecretAgreement(browser.PublicKey);
        var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, [], LinkInfo);
        CryptographicOperations.ZeroMemory(shared);

        if (!sealedKey.StartsWith("v1.", StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown sealed key format.");
        }

        var payload = Convert.FromBase64String(sealedKey[3..]);
        if (payload.Length < 12 + 16)
        {
            throw new CryptographicException("Sealed key is too short.");
        }

        var plaintext = new byte[payload.Length - 12 - 16];
        using var aes = new AesGcm(aesKey, 16);
        aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(12, plaintext.Length), payload.AsSpan(payload.Length - 16), plaintext, Encoding.ASCII.GetBytes(Challenge));
        CryptographicOperations.ZeroMemory(aesKey);
        return plaintext;
    }

    public static string FingerprintOf(string deviceKey)
    {
        var digest = SHA256.HashData(FromBase64Url(deviceKey));
        var number = ((uint)digest[0] << 24 | (uint)digest[1] << 16 | (uint)digest[2] << 8 | digest[3]) % 100_000_000;
        var digits = number.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        return $"{digits[..4]} {digits[4..]}";
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
    }

    public void Dispose() => _key.Dispose();
}
