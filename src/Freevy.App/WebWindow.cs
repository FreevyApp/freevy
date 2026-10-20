using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Freevy.App;

/// <summary>Shared WebView2 setup for the app's windows.</summary>
internal static class WebHost
{
    public const string HostName = "app.freevy.local";

    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>Browser data lives outside the snippets folder so backups and the file watcher ignore it.</summary>
    public static Task<CoreWebView2Environment> Environment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Freevy", "WebView2"));

    public static string UiFolder => Path.Combine(AppContext.BaseDirectory, "ui");

    public static async Task InitializeAsync(WebView2 view)
    {
        await view.EnsureCoreWebView2Async(await Environment());
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(HostName, UiFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = System.Environment.GetEnvironmentVariable("FREEVY_DEVTOOLS") == "1";

        // The UI is local; anything else opens in the user's browser.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
    }
}

/// <summary>The main window: frameless, its title bar and controls are drawn by the web UI.</summary>
internal sealed class WebWindow : Form
{
    private const int ResizeBorder = 4;
    private readonly AppCore _core;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 18, 33) };
    private Bridge? _bridge;
    private bool _ready;

    public static bool Offscreen { get; set; }

    protected override bool ShowWithoutActivation => Offscreen;

    public WebWindow(AppCore core)
    {
        _core = core;
        Text = "Freevy";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1240, 780);
        MinimumSize = new Size(760, 480);
        // A thin frame around the web view leaves room for resize hit-testing; the page tints it to its theme.
        Padding = new Padding(ResizeBorder);
        BackColor = Color.FromArgb(40, 46, 70);
        Controls.Add(_view);

        core.Changed += change => _bridge?.Emit("change", change.ToString().ToLowerInvariant());
        core.Notice += (message, problem) => _bridge?.Emit("notice", new { message, error = problem });
        Load += async (_, _) => await InitializeAsync();
        // Covers maximizing from the page, Aero Snap and Win+Up alike.
        Resize += (_, _) => SyncWindowState();
        if (Offscreen)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
        }
    }

    public bool IsReady => _ready;

    private async Task InitializeAsync()
    {
        await WebHost.InitializeAsync(_view);
        _bridge = new Bridge(_core, this, WindowAction, json => _view.CoreWebView2.PostWebMessageAsString(json));
        _view.CoreWebView2.WebMessageReceived += async (_, e) => await _bridge.HandleAsync(e.TryGetWebMessageAsString());
        _view.CoreWebView2.NavigationCompleted += (_, _) => SyncWindowState(force: true);
        _view.CoreWebView2.Navigate($"https://{WebHost.HostName}/index.html");
        _ready = true;
    }

    private FormWindowState _reportedState = (FormWindowState)(-1);

    // The frame and the page's resize handles only make sense on a normal window.
    private void SyncWindowState(bool force = false)
    {
        if (WindowState == FormWindowState.Minimized) return;
        Padding = new Padding(WindowState == FormWindowState.Maximized ? 0 : ResizeBorder);
        if (_reportedState == WindowState && !force) return;
        _reportedState = WindowState;
        _bridge?.Emit("window", new { maximized = WindowState == FormWindowState.Maximized });
    }

    private void WindowAction(string action)
    {
        switch (action)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "maximize":
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case "close":
                Hide();
                break;
            case var frame when frame.StartsWith("frame:", StringComparison.Ordinal):
                // "frame:#rrggbb" from the page, so the resize border matches the theme.
                try
                {
                    BackColor = ColorTranslator.FromHtml(frame[6..]);
                    _view.DefaultBackgroundColor = BackColor;
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    // Ignore malformed colors.
                }

                break;
            case "drag":
                // Started from a mousedown on the web title bar: hand the drag to Windows.
                if (WindowState != FormWindowState.Maximized)
                {
                    Native.BeginWindowDrag(Handle);
                }

                break;
            case var resize when resize.StartsWith("resize:", StringComparison.Ordinal):
                // Started from a mousedown on one of the page's edge handles: hand the resize to Windows.
                if (WindowState == FormWindowState.Normal && ResizeEdges.TryGetValue(resize[7..], out var hit))
                {
                    Native.BeginWindowResize(Handle, hit);
                }

                break;
        }
    }

    public void ShowAndActivate()
    {
        Show();
        _bridge?.Emit("opened");
        if (Offscreen)
        {
            return;
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Keep the native minimize/maximize animations and taskbar behavior of a normal window.
            cp.Style |= WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            var point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            var left = point.X < ResizeBorder;
            var right = point.X >= ClientSize.Width - ResizeBorder;
            var top = point.Y < ResizeBorder;
            var bottom = point.Y >= ClientSize.Height - ResizeBorder;
            m.Result = (left, right, top, bottom) switch
            {
                (true, _, true, _) => HTTOPLEFT,
                (_, true, true, _) => HTTOPRIGHT,
                (true, _, _, true) => HTBOTTOMLEFT,
                (_, true, _, true) => HTBOTTOMRIGHT,
                (true, _, _, _) => HTLEFT,
                (_, true, _, _) => HTRIGHT,
                (_, _, true, _) => HTTOP,
                (_, _, _, true) => HTBOTTOM,
                _ => m.Result,
            };
            return;
        }

        base.WndProc(ref m);
    }

    private static readonly Dictionary<string, int> ResizeEdges = new()
    {
        ["w"] = 10, ["e"] = 11, ["n"] = 12, ["nw"] = 13, ["ne"] = 14, ["s"] = 15, ["sw"] = 16, ["se"] = 17,
    };

    private const int WM_NCHITTEST = 0x84;
    private static readonly IntPtr HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int WS_MINIMIZEBOX = 0x20000;
    private const int WS_MAXIMIZEBOX = 0x10000;
    private const int WS_SYSMENU = 0x80000;
}
