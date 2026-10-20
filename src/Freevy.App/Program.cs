using Freevy.Core;

namespace Freevy.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\Freevy", out var first);
        if (!first)
        {
            // Ask the running instance to bring its window up instead of starting a second copy.
            if (EventWaitHandle.TryOpenExisting(TrayContext.ShowWindowEventName, out var show))
            {
                show.Set();
                show.Dispose();
            }

            return;
        }

        ApplicationConfiguration.Initialize();

        // --tray starts without opening the window (for autostart); --data <folder> uses another data folder.
        var startHidden = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        // For automated UI tests: the window exists but sits off-screen and never takes focus.
        WebWindow.Offscreen = args.Contains("--offscreen", StringComparer.OrdinalIgnoreCase);
        var dataIndex = Array.FindIndex(args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
        var directory = dataIndex >= 0 && dataIndex + 1 < args.Length
            ? Path.GetFullPath(args[dataIndex + 1])
            : DataStore.DefaultDirectory;
        if (dataIndex < 0)
        {
            MigrateFromOzanExtender(directory);
        }

        Application.Run(new TrayContext(new DataStore(directory), startHidden));
    }

    /// <summary>The app was called OzanExtender before; move its data folder and startup entry over once.</summary>
    private static void MigrateFromOzanExtender(string directory)
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OzanExtender");
        if (!Directory.Exists(directory) && Directory.Exists(legacy))
        {
            Directory.Move(legacy, directory);
        }

        Autostart.MigrateLegacyEntry();
    }
}
