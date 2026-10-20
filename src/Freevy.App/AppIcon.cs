namespace Freevy.App;

/// <summary>
/// The Freevy mark (an F made of a text cursor and text lines, on a turquoise rounded square), from
/// Assets/freevy.ico. build/make-icon.ps1 draws that file from design/logo/freevy-icon.svg.
/// </summary>
internal static class AppIcon
{
    private static Icon? _window;
    private static Icon? _tray;

    /// <summary>Every size, so Windows picks the right one for the title bar, taskbar and Alt+Tab.</summary>
    public static Icon Get() => _window ??= Load(null);

    /// <summary>The notification area size for the current DPI (16 px at 100 %).</summary>
    public static Icon Tray() => _tray ??= Load(SystemInformation.SmallIconSize);

    private static Icon Load(Size? size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Freevy.Icon")
            ?? throw new InvalidOperationException("The Freevy icon resource is missing.");
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }
}
