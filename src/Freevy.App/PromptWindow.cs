using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace Freevy.App;

/// <summary>
/// Small always-on-top master password prompt in the app's design, used when a protected
/// abbreviation is typed while locked, and from the tray menu.
/// </summary>
internal sealed class PromptWindow : Form
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly AppCore _core;
    private readonly string _reason;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 18, 33) };
    private readonly TaskCompletionSource<bool> _result = new();

    private PromptWindow(AppCore core, string reason)
    {
        _core = core;
        _reason = reason;
        Text = "Unlock Freevy";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 268);
        TopMost = true;
        ShowInTaskbar = true;
        Controls.Add(_view);
        Load += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => _result.TrySetResult(false);
    }

    /// <summary>Shows the prompt and completes with true once the vault is unlocked.</summary>
    public static Task<bool> AskAsync(AppCore core, string reason)
    {
        var window = new PromptWindow(core, reason);
        window.Show();
        window.Activate();
        return window._result.Task;
    }

    private async Task InitializeAsync()
    {
        await WebHost.InitializeAsync(_view);
        var view = _view.CoreWebView2;
        view.WebMessageReceived += (_, e) =>
        {
            using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = message.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    Post(new { type = "init", reason = _reason, minutes = _core.Settings.UnlockMinutes, theme = _core.Settings.Theme });
                    break;
                case "unlock":
                    var ok = _core.Unlock(root.GetProperty("password").GetString() ?? "", root.GetProperty("minutes").GetInt32());
                    if (ok)
                    {
                        _result.TrySetResult(true);
                        Close();
                    }
                    else
                    {
                        Post(new { type = "error", message = "Wrong master password." });
                    }

                    break;
                case "cancel":
                    Close();
                    break;
                case "drag":
                    Native.BeginWindowDrag(Handle);
                    break;
            }
        };
        view.Navigate($"https://{WebHost.HostName}/prompt.html");
    }

    private void Post(object message) => _view.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message, Json));
}
