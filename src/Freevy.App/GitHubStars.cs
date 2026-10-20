using System.Net.Http.Headers;
using System.Text.Json;

namespace Freevy.App;

/// <summary>
/// Star count of the public repository for the title bar chip. Asked from GitHub's public API
/// (the WebView's content security policy keeps the page itself from calling it), at most once
/// an hour; on failure the last known count, or none, is shown.
/// </summary>
internal static class GitHubStars
{
    private static readonly HttpClient Http = CreateClient();
    private static int? _count;
    private static DateTime _checkedUtc = DateTime.MinValue;

    public static async Task<int?> GetAsync()
    {
        if (DateTime.UtcNow - _checkedUtc < TimeSpan.FromHours(1))
        {
            return _count;
        }

        _checkedUtc = DateTime.UtcNow;
        try
        {
            using var response = await Http.GetAsync("https://api.github.com/repos/FreevyApp/freevy");
            if (response.IsSuccessStatusCode)
            {
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                if (json.RootElement.TryGetProperty("stargazers_count", out var stars) && stars.TryGetInt32(out var count))
                {
                    _count = count;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline or rate limited: keep the last known count and try again in an hour.
        }

        return _count;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Freevy", null));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
