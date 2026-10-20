using System.Security.Cryptography;
using System.Text;

namespace Freevy.App;

/// <summary>Encrypts small secrets (SFTP password, key passphrase) for the current Windows user with DPAPI.</summary>
internal static class Secrets
{
    // Keeps the pre-rename value: secrets saved by OzanExtender must stay readable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OzanExtender.Secrets.v1");

    public static string Protect(string plain) =>
        plain.Length == 0
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Returns null when the value cannot be decrypted, e.g. settings copied from another PC.</summary>
    public static string? Unprotect(string protectedValue)
    {
        if (protectedValue.Length == 0)
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
