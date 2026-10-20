namespace Freevy.Core.Backup;

public static class BackupSchedule
{
    /// <summary>Changes are batched: a change triggers a backup once nothing changed for this long.</summary>
    public static readonly TimeSpan ChangeQuietPeriod = TimeSpan.FromMinutes(2);

    public static bool IsDue(BackupSettings settings, BackupState state, DateTime nowUtc)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        var last = state.LastRunUtc;
        if (settings.OnChange && state.LastChangeUtc is { } changed
            && (last is null || changed > last)
            && nowUtc - changed >= ChangeQuietPeriod)
        {
            return true;
        }

        return settings.IntervalHours > 0 && (last is null || nowUtc - last >= TimeSpan.FromHours(settings.IntervalHours));
    }
}

public sealed record BackupResult(bool Success, string Summary);

public static class BackupRunner
{
    /// <summary>Builds one archive and sends it to every target. One failing target does not stop the others.</summary>
    public static BackupResult Run(DataStore store, IReadOnlyList<IBackupTarget> targets, int keep)
    {
        if (targets.Count == 0)
        {
            return new BackupResult(false, "No backup destination is enabled.");
        }

        var temp = Path.Combine(Path.GetTempPath(), "Freevy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var zip = Path.Combine(temp, BackupArchive.FileName(DateTime.Now));
            BackupArchive.Create(store, zip);

            var failures = new List<string>();
            foreach (var target in targets)
            {
                try
                {
                    target.Upload(zip, keep);
                }
                catch (Exception ex)
                {
                    failures.Add($"{target.Name}: {ex.Message}");
                }
            }

            var succeeded = targets.Count - failures.Count;
            var summary = failures.Count == 0
                ? $"Backed up to {string.Join(", ", targets.Select(t => t.Name))}."
                : $"{succeeded} of {targets.Count} destinations succeeded. " + string.Join(" ", failures);
            return new BackupResult(failures.Count == 0, summary);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
