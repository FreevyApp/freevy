namespace Freevy.Core.Sync;

public sealed record SyncSettings
{
    public const string DefaultServerUrl = "https://freevy.app";

    public string ServerUrl { get; init; } = DefaultServerUrl;

    public string Email { get; init; } = "";

    public string DeviceName { get; init; } = Environment.MachineName;

    /// <summary>Device token, encrypted for the current Windows user (DPAPI).</summary>
    public string ProtectedToken { get; init; } = "";

    /// <summary>The account's data key, encrypted for the current Windows user (DPAPI).</summary>
    public string ProtectedDataKey { get; init; } = "";

    /// <summary>Sync at least this often while signed in.</summary>
    public int IntervalMinutes { get; init; } = 5;

    public bool IsSignedIn => ProtectedToken.Length > 0 && ProtectedDataKey.Length > 0;
}
