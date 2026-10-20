using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Freevy.Core.Security;

namespace Freevy.Core.Sync;

/// <summary>
/// Converts snippets and the vault header to and from the synced item JSON described in
/// freevy-web/docs/crypto.md, and hashes them to detect changes.
/// </summary>
public static class SyncItems
{
    /// <summary>Fixed id of the vault header item.</summary>
    public static readonly Guid VaultId = Guid.Parse("00000000-0000-4000-8000-000000000001");

    public static JsonElement FromSnippet(Snippet snippet)
    {
        var json = new JsonObject
        {
            ["type"] = "snippet",
            ["v"] = 1,
            ["abbreviations"] = new JsonArray([.. snippet.Abbreviations.Select(a => (JsonNode?)a)]),
        };
        if (snippet.Protected)
        {
            json["sealed_expansion"] = snippet.Encrypted ?? "";
        }
        else
        {
            json["expansion"] = snippet.Expansion;
        }

        json["description"] = snippet.Description;
        json["folder"] = snippet.Folder;
        json["case"] = snippet.Case.ToString();
        json["trigger"] = snippet.Trigger.ToString();
        json["created"] = FormatDate(snippet.Created);
        json["modified"] = FormatDate(snippet.Modified);
        return JsonSerializer.SerializeToElement(json);
    }

    /// <summary>Returns null for item types or versions this client does not understand.</summary>
    public static Snippet? ToSnippet(Guid id, JsonElement item)
    {
        if (Text(item, "type") != "snippet" || !item.TryGetProperty("v", out var version) || version.GetInt32() != 1)
        {
            return null;
        }

        var abbreviations = item.TryGetProperty("abbreviations", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToList()
            : [];
        var sealedExpansion = Text(item, "sealed_expansion");

        return new Snippet(
            abbreviations,
            sealedExpansion is null ? Text(item, "expansion") ?? "" : "",
            Text(item, "description") ?? "",
            Enum.TryParse<CaseMode>(Text(item, "case"), out var caseMode) ? caseMode : CaseMode.Adapt,
            Enum.TryParse<TriggerFlags>(Text(item, "trigger"), out var trigger) ? trigger : TriggerFlags.WordStart | TriggerFlags.AfterDelimiter,
            Text(item, "folder") ?? "",
            ParseDate(Text(item, "created")),
            ParseDate(Text(item, "modified")),
            sealedExpansion is not null,
            sealedExpansion,
            id);
    }

    public static JsonElement FromVault(VaultHeader header) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "vault",
        ["v"] = 1,
        ["salt"] = header.Salt,
        ["iterations"] = header.Iterations,
        ["verifier"] = header.Verifier,
    });

    public static VaultHeader? ToVault(JsonElement item) =>
        Text(item, "type") == "vault" && item.TryGetProperty("iterations", out var iterations)
            ? new VaultHeader(Text(item, "salt") ?? "", iterations.GetInt32(), Text(item, "verifier") ?? "")
            : null;

    /// <summary>SHA-256 of the compact item JSON; equal content gives equal hashes on every client.</summary>
    public static string Hash(JsonElement item) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item))));

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FormatDate(DateTime? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
}
