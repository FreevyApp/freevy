using System.Text.Json;
using Freevy.Core.Macros;
using Microsoft.Web.WebView2.WinForms;

namespace Freevy.App;

/// <summary>
/// Asks for the %(Input)/%(Fill) values of a snippet before Freevy types it, in the same small,
/// always-on-top window style as the master password prompt.
/// </summary>
internal sealed class FillWindow : Form
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly IReadOnlyList<InputRequest> _requests;
    private readonly string _theme;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 18, 33) };
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _result = new();

    private FillWindow(IReadOnlyList<InputRequest> requests, string theme)
    {
        _requests = requests;
        _theme = theme;
        Text = "Fill in";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, Math.Min(560, 168 + 74 * requests.Count));
        TopMost = true;
        ShowInTaskbar = true;
        Controls.Add(_view);
        Load += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => _result.TrySetResult(null);
    }

    public static Task<IReadOnlyDictionary<string, string>?> AskAsync(IReadOnlyList<InputRequest> requests, string theme)
    {
        var window = new FillWindow(requests, theme);
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
                    Post(new { type = "init", theme = _theme, fields = _requests.Select(r => new { key = r.Key, label = r.Label }) });
                    break;
                case "submit":
                    var values = root.GetProperty("values").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
                    _result.TrySetResult(values);
                    Close();
                    break;
                case "cancel":
                    Close();
                    break;
                case "drag":
                    Native.BeginWindowDrag(Handle);
                    break;
            }
        };
        view.Navigate($"https://{WebHost.HostName}/fill.html");
    }

    private void Post(object message) => _view.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message, Json));
}
