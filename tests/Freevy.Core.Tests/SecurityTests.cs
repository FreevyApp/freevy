using System.Security.Cryptography;
using Freevy.Core;
using Freevy.Core.Import.Breevy;
using Freevy.Core.Security;

namespace Freevy.Core.Tests;

public class SecurityTests
{
    // Low iteration count keeps tests fast; production uses Vault.DefaultIterations.
    private const int Iterations = 1_000;

    [Fact]
    public void Correct_password_unlocks_and_round_trips()
    {
        var (header, vault) = Vault.Create("correct horse", Iterations);
        var sealedText = vault.Encrypt("s3cr3t ünïcode 🔑");

        using var unlocked = Vault.TryUnlock(header, "correct horse");

        Assert.NotNull(unlocked);
        Assert.Equal("s3cr3t ünïcode 🔑", unlocked.Decrypt(sealedText));
        Assert.DoesNotContain("s3cr3t", sealedText);
    }

    [Fact]
    public void Wrong_password_does_not_unlock()
    {
        var (header, _) = Vault.Create("correct horse", Iterations);

        Assert.Null(Vault.TryUnlock(header, "wrong horse!"));
    }

    [Fact]
    public void Short_password_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Vault.Create("short", Iterations));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_and_tampering_is_detected()
    {
        var (_, vault) = Vault.Create("correct horse", Iterations);
        var first = vault.Encrypt("same");
        var second = vault.Encrypt("same");
        Assert.NotEqual(first, second);

        var bytes = Convert.FromBase64String(first);
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => vault.Decrypt(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Disposed_vault_refuses_work()
    {
        var (_, vault) = Vault.Create("correct horse", Iterations);
        vault.Dispose();

        Assert.Throws<ObjectDisposedException>(() => vault.Encrypt("x"));
    }

    [Fact]
    public void Protect_unprotect_and_rekey_keep_the_expansion()
    {
        var snippet = BvyParser.Parse(ParserTests.Fixture)[2];
        var (_, oldVault) = Vault.Create("old password", Iterations);
        var (_, newVault) = Vault.Create("new password", Iterations);

        var protectedSnippet = SnippetProtection.Protect(snippet, oldVault);
        Assert.True(protectedSnippet.Protected);
        Assert.Equal("", protectedSnippet.Expansion);
        Assert.Equal(snippet.Expansion, SnippetProtection.Reveal(protectedSnippet, oldVault));

        var rekeyed = SnippetProtection.Rekey([protectedSnippet], oldVault, newVault).Single();
        Assert.Equal(snippet.Expansion, SnippetProtection.Reveal(rekeyed, newVault));
        Assert.ThrowsAny<CryptographicException>(() => SnippetProtection.Reveal(rekeyed, oldVault));

        Assert.Equal(snippet, SnippetProtection.Unprotect(rekeyed, newVault));
    }

    [Fact]
    public void Engine_reports_protected_snippets_without_their_text()
    {
        var (_, vault) = Vault.Create("correct horse", Iterations);
        var snippets = BvyParser.Parse(ParserTests.Fixture).Select(s => SnippetProtection.Protect(s, vault)).ToList();
        var engine = new ExpansionEngine(snippets, Settings.DefaultDelimiters);

        Expansion? result = null;
        foreach (var c in "teh ")
        {
            result = engine.Type(c) ?? result;
        }

        Assert.NotNull(result);
        Assert.True(result.Snippet.Protected);
        Assert.Equal("teh ", result.Deleted);
        Assert.Equal(" ", result.Text);
        Assert.Equal(" ", result.Suffix);
    }
}
