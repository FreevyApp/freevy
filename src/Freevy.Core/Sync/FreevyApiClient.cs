using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Freevy.Core.Sync;

public sealed record LoginResult(string Token, KdfParameters Kdf, string WrappedDataKey);

/// <summary>Raised for answers the user should see, such as wrong credentials or an unconfirmed email.</summary>
public sealed class FreevyApiException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Client for the freevy.app account and sync API.</summary>
public sealed class FreevyApiClient : ISyncTransport, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public FreevyApiClient(Uri serverUrl, string? token = null)
    {
        _http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(serverUrl, "/api/"), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Freevy", typeof(FreevyApiClient).Assembly.GetName().Version?.ToString(3) ?? "0"));
        if (token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<KdfParameters> PreloginAsync(string email, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, "prelogin", new { email }, cancellationToken);
        return (await Read<PreloginResponse>(response, cancellationToken)).Kdf;
    }

    public async Task<LoginResult> LoginAsync(string email, string authHash, string deviceName, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, "login", new { email, authHash, deviceName }, cancellationToken);
        var login = await Read<LoginResponse>(response, cancellationToken);
        return new LoginResult(login.Token, login.Kdf, login.WrappedDataKey);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, "logout", null, cancellationToken);

    public async Task<PullPage> PullAsync(long since, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, $"sync?since={since}", null, cancellationToken);
        var page = await Read<PullResponse>(response, cancellationToken);
        return new PullPage(page.Items.Select(i => i.ToRemote()).ToList(), page.Cursor, page.More);
    }

    public async Task<IReadOnlyList<PushResult>> PushAsync(IReadOnlyList<PushChange> changes, CancellationToken cancellationToken)
    {
        var body = new
        {
            changes = changes.Select(c => new
            {
                id = c.Id.ToString("D"),
                base_revision = c.BaseRevision,
                ciphertext = c.Deleted ? null : c.Ciphertext,
                deleted = c.Deleted,
            }),
        };
        var response = await SendAsync(HttpMethod.Post, "sync", body, cancellationToken, includeNulls: true);
        var push = await Read<PushResponse>(response, cancellationToken);
        return push.Results.Select(r => new PushResult(r.Id, r.Status == "ok", r.Revision, r.Current?.ToRemote())).ToList();
    }

    /// <summary>Earlier encrypted versions the server kept (newest first).</summary>
    public async Task<IReadOnlyList<HistoryVersion>> HistoryAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "sync/history", null, cancellationToken);
        return (await Read<HistoryResponse>(response, cancellationToken)).Versions
            .Select(v => new HistoryVersion(v.Id, v.Revision, v.Ciphertext, v.ReplacedAt, v.WasDeleted))
            .ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool includeNulls = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: includeNulls ? new JsonSerializerOptions(Json) { DefaultIgnoreCondition = JsonIgnoreCondition.Never } : Json);
        }

        var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var message = await ErrorMessage(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized && _http.DefaultRequestHeaders.Authorization is not null)
        {
            throw new SignedOutException("This computer was signed out of Freevy. Sign in again to keep syncing.");
        }

        throw new FreevyApiException(message, response.StatusCode);
    }

    private static async Task<string> ErrorMessage(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, cancellationToken);
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                return error.Message;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Not a JSON error body; fall back to the status.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
            HttpStatusCode.RequestEntityTooLarge => "Your Freevy storage is full.",
            _ => $"The Freevy server answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new FreevyApiException("The Freevy server sent an empty answer.", response.StatusCode);

    /// <summary>
    /// The Windows resolver does not resolve *.localhost, which local development sites use;
    /// connect those to the loopback address directly.
    /// </summary>
    private static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var endPoint = host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                ? new DnsEndPoint(IPAddress.Loopback.ToString(), context.DnsEndPoint.Port)
                : context.DnsEndPoint;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public void Dispose() => _http.Dispose();

    private sealed record PreloginResponse(KdfParameters Kdf);

    private sealed record LoginResponse(string Token, KdfParameters Kdf, string WrappedDataKey);

    private sealed record ErrorResponse(string? Message);

    private sealed record WireItem(Guid Id, long Revision, bool Deleted, string? Ciphertext)
    {
        public RemoteItem ToRemote() => new(Id, Revision, Deleted, Ciphertext);
    }

    private sealed record PullResponse(List<WireItem> Items, long Cursor, bool More);

    private sealed record WireResult(Guid Id, string Status, long? Revision, WireItem? Current);

    private sealed record PushResponse(List<WireResult> Results, long Revision);

    private sealed record WireVersion(Guid Id, long Revision, string Ciphertext, DateTimeOffset ReplacedAt, bool WasDeleted);

    private sealed record HistoryResponse(List<WireVersion> Versions);
}
