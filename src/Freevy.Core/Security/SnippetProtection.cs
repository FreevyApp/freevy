namespace Freevy.Core.Security;

public static class SnippetProtection
{
    public static Snippet Protect(Snippet snippet, Vault vault) =>
        snippet.Protected
            ? snippet
            : snippet with { Protected = true, Encrypted = vault.Encrypt(snippet.Expansion), Expansion = "" };

    public static Snippet Unprotect(Snippet snippet, Vault vault) =>
        snippet.Protected
            ? snippet with { Protected = false, Expansion = Reveal(snippet, vault), Encrypted = null }
            : snippet;

    /// <summary>The plain expansion of a snippet, decrypting it when protected.</summary>
    public static string Reveal(Snippet snippet, Vault vault) =>
        snippet.Protected ? vault.Decrypt(snippet.Encrypted ?? "") : snippet.Expansion;

    /// <summary>Re-encrypts every protected snippet from one master key to another.</summary>
    public static IReadOnlyList<Snippet> Rekey(IEnumerable<Snippet> snippets, Vault from, Vault to) =>
        snippets.Select(s => s.Protected ? s with { Encrypted = to.Encrypt(from.Decrypt(s.Encrypted ?? "")) } : s).ToList();
}
