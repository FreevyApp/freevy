using System.Security.Cryptography;
using System.Text.Json;
using Freevy.Core.Sync;

namespace Freevy.Core.Tests;

/// <summary>
/// Browser sign-in against a vector sealed by the website's JavaScript (freevy-web
/// tests/js/fixtures/device-link.json), so the app can open what the browser sends.
/// </summary>
public class BrowserSignInTests
{
    private static readonly JsonElement Vector =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "device-link.json"))).RootElement;

    private static string Text(string name) => Vector.GetProperty(name).GetString()!;

    private static BrowserSignIn FromVector(string? verifier = null) =>
        BrowserSignIn.FromKey(Convert.FromBase64String(Text("device_private_pkcs8")), verifier ?? Text("verifier"), new Uri("https://freevy.app"));

    [Fact]
    public void Opens_the_data_key_the_browser_sealed()
    {
        using var signIn = FromVector();

        Assert.Equal(Text("device_key"), signIn.DeviceKey);
        Assert.Equal(Text("challenge"), signIn.Challenge);
        Assert.Equal(Text("fingerprint"), signIn.Fingerprint);
        Assert.Equal(Text("data_key"), Convert.ToBase64String(signIn.OpenDataKey(Text("browser_key"), Text("sealed_key"))));
    }

    [Fact]
    public void A_different_challenge_cannot_open_it()
    {
        using var signIn = FromVector(verifier: new string('A', 43));

        Assert.ThrowsAny<CryptographicException>(() => signIn.OpenDataKey(Text("browser_key"), Text("sealed_key")));
    }

    [Fact]
    public void Another_device_key_cannot_open_it()
    {
        using var other = BrowserSignIn.Start(new Uri("https://freevy.app"), "Other PC");

        Assert.ThrowsAny<CryptographicException>(() => other.OpenDataKey(Text("browser_key"), Text("sealed_key")));
    }

    [Fact]
    public void Start_builds_the_website_link()
    {
        using var signIn = BrowserSignIn.Start(new Uri("https://freevy.app"), "Ozan's PC");
        var url = signIn.BrowserUrl.AbsoluteUri;

        Assert.StartsWith("https://freevy.app/link?device=Ozan%27s%20PC&key=", url);
        Assert.Contains($"challenge={signIn.Challenge}", url);
        Assert.Contains($"state={signIn.State}", url);
        Assert.Equal(43, signIn.Challenge.Length);
        Assert.False(signIn.IsExpired(DateTime.UtcNow));
        Assert.True(signIn.IsExpired(DateTime.UtcNow + BrowserSignIn.Lifetime + TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("freevy://link?code=abc&state=xyz", "abc", "xyz")]
    [InlineData("FREEVY://link/?state=s1&code=c%2B1", "c+1", "s1")]
    public void Parses_the_link_back_from_the_browser(string link, string code, string state)
    {
        Assert.Equal((code, state), BrowserSignIn.ParseLink(link));
    }

    [Theory]
    [InlineData("https://freevy.app/link?code=abc&state=xyz")]
    [InlineData("freevy://other?code=abc&state=xyz")]
    [InlineData("freevy://link?code=abc")]
    [InlineData("not a url")]
    public void Ignores_anything_else(string link)
    {
        Assert.Null(BrowserSignIn.ParseLink(link));
    }
}
