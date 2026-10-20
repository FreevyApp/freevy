using System.Security.Cryptography;
using System.Text.Json;
using Freevy.Core.Sync;

namespace Freevy.Core.Tests;

/// <summary>
/// Checks the C# account crypto against vectors produced by the website's JavaScript
/// (freevy-web/scripts/crypto-vectors.mjs), so both clients can read each other's data.
/// </summary>
public class AccountCryptoTests
{
    private static readonly JsonElement Vectors =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "crypto-vectors.json"))).RootElement;

    private static string Text(string name) => Vectors.GetProperty(name).GetString()!;

    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element);

    private static KdfParameters Kdf()
    {
        var kdf = Vectors.GetProperty("kdf");
        return new KdfParameters(
            kdf.GetProperty("algorithm").GetString()!,
            kdf.GetProperty("salt").GetString()!,
            kdf.GetProperty("memory").GetInt32(),
            kdf.GetProperty("iterations").GetInt32(),
            kdf.GetProperty("parallelism").GetInt32());
    }

    [Fact]
    public void Master_key_and_auth_hash_match_the_browser()
    {
        var masterKey = AccountCrypto.DeriveMasterKey(Text("password"), Kdf());

        Assert.Equal(Text("masterKey"), Convert.ToBase64String(masterKey));
        Assert.Equal(Text("authHash"), AccountCrypto.DeriveAuthHash(masterKey, Text("password")));
    }

    [Fact]
    public void Data_key_wrapped_in_the_browser_unwraps_here()
    {
        var masterKey = Convert.FromBase64String(Text("masterKey"));

        Assert.Equal(Text("dataKey"), Convert.ToBase64String(AccountCrypto.UnwrapDataKey(masterKey, Text("wrappedDataKey"))));
        Assert.ThrowsAny<CryptographicException>(() => AccountCrypto.UnwrapDataKey(new byte[32], Text("wrappedDataKey")));
    }

    [Fact]
    public void Item_sealed_in_the_browser_opens_here_and_is_bound_to_its_id()
    {
        var itemKey = AccountCrypto.ItemKey(Convert.FromBase64String(Text("dataKey")));
        var id = Guid.Parse(Text("itemId"));

        var item = AccountCrypto.OpenItem(itemKey, id, Text("sealedItem"));

        Assert.Equal(Compact(Vectors.GetProperty("item")), Compact(item));
        Assert.ThrowsAny<CryptographicException>(() => AccountCrypto.OpenItem(itemKey, Guid.NewGuid(), Text("sealedItem")));
    }

    [Fact]
    public void Items_sealed_here_round_trip_with_the_same_layout_and_padding()
    {
        var itemKey = AccountCrypto.ItemKey(Convert.FromBase64String(Text("dataKey")));
        var id = Guid.Parse(Text("itemId"));
        var item = Vectors.GetProperty("item");

        var sealedHere = AccountCrypto.SealItem(itemKey, id, item);

        Assert.Equal(Compact(item), Compact(AccountCrypto.OpenItem(itemKey, id, sealedHere)));
        // Same plaintext size after padding means the same sealed length as the browser's.
        Assert.Equal(Text("sealedItem").Length, sealedHere.Length);
        Assert.StartsWith("v1.", sealedHere);
    }
}
