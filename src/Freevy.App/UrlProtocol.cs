using Microsoft.Win32;

namespace Freevy.App;

/// <summary>
/// Registers freevy:// for the current user (no admin rights needed), so the website can hand a
/// browser sign-in back to the app. Re-registered on every start, which also follows the app when
/// it moves to another folder.
/// </summary>
internal static class UrlProtocol
{
    public const string Scheme = "freevy";
    private const string KeyPath = @"Software\Classes\" + Scheme;

    public static void Register(string executable)
    {
        var command = $"\"{executable}\" \"%1\"";
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key.OpenSubKey(@"shell\open\command")?.GetValue(null) as string == command)
        {
            return;
        }

        key.SetValue(null, "URL:Freevy");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue(null, $"\"{executable}\",0");
        }

        using var open = key.CreateSubKey(@"shell\open\command");
        open.SetValue(null, command);
    }

    public static bool IsLink(string argument) => argument.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);
}
