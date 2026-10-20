using Microsoft.Win32;

namespace Freevy.App;

/// <summary>"Start with Windows" through the per-user Run key; the registry is the source of truth.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Freevy";
    private const string LegacyValueName = "OzanExtender";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value
                && value.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Replaces an OzanExtender startup entry with one for this executable.</summary>
    public static void MigrateLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is null)
        {
            return;
        }

        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        key.SetValue(ValueName, Command);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
