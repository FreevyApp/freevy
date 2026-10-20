using Freevy.Core;
using Freevy.Core.Backup;
using Freevy.Core.Import.Breevy;
using Freevy.Core.Security;

namespace Freevy.Core.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FreevyTests", Guid.NewGuid().ToString("N"));

    private DataStore NewStore(string name) => new(Path.Combine(_root, name));

    [Fact]
    public void Archive_round_trips_snippets_and_vault()
    {
        var source = NewStore("source");
        var (header, vault) = Vault.Create("correct horse", 1_000);
        var snippets = BvyParser.Parse(ParserTests.Fixture).Select((s, i) => i == 0 ? SnippetProtection.Protect(s, vault) : s).ToList();
        source.SaveSnippets(snippets);
        source.SaveVault(header);
        var zip = Path.Combine(_root, BackupArchive.FileName(DateTime.Now));
        BackupArchive.Create(source, zip);

        var target = NewStore("target");
        var count = BackupArchive.Restore(zip, target);

        Assert.Equal(snippets.Count, count);
        Assert.Equal(header, target.LoadVault());
        using var unlocked = Vault.TryUnlock(target.LoadVault()!, "correct horse");
        Assert.Equal("the", SnippetProtection.Reveal(target.LoadSnippets()[0], unlocked!));
    }

    [Fact]
    public void Restore_without_vault_removes_a_stale_vault()
    {
        var source = NewStore("source");
        source.SaveSnippets(BvyParser.Parse(ParserTests.Fixture));
        var zip = Path.Combine(_root, BackupArchive.FileName(DateTime.Now));
        BackupArchive.Create(source, zip);

        var target = NewStore("target");
        target.SaveVault(Vault.Create("correct horse", 1_000).Header);
        BackupArchive.Restore(zip, target);

        Assert.Null(target.LoadVault());
    }

    [Fact]
    public void Restore_rejects_files_that_are_not_backups()
    {
        var zip = Path.Combine(_root, "random.zip");
        Directory.CreateDirectory(_root);
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("hello.txt");
        }

        Assert.Throws<InvalidDataException>(() => BackupArchive.Restore(zip, NewStore("target")));
    }

    [Fact]
    public void Folder_target_keeps_only_the_newest_backups_and_ignores_other_files()
    {
        var folder = Path.Combine(_root, "drive");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "keep me");
        var start = new DateTime(2026, 10, 1, 8, 0, 0);
        for (var day = 0; day < 4; day++)
        {
            File.WriteAllText(Path.Combine(folder, BackupArchive.FileName(start.AddDays(day))), "old");
        }

        var newest = Path.Combine(_root, BackupArchive.FileName(start.AddDays(10)));
        File.WriteAllText(newest, "new");
        new FolderBackupTarget("Drive", folder).Upload(newest, keep: 3);

        var names = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
        [
            BackupArchive.FileName(start.AddDays(2)),
            BackupArchive.FileName(start.AddDays(3)),
            BackupArchive.FileName(start.AddDays(10)),
            "notes.txt",
        ], names);
    }

    [Fact]
    public void Retention_orders_pre_rename_and_new_backups_by_time()
    {
        string[] names =
        [
            "OzanExtender-backup-20261001-080000.zip",
            "OzanExtender-backup-20261003-080000.zip",
            BackupArchive.FileName(new DateTime(2026, 10, 2, 8, 0, 0)),
            BackupArchive.FileName(new DateTime(2026, 10, 4, 8, 0, 0)),
            "unrelated.zip",
        ];

        Assert.Equal(
            ["Freevy-backup-20261002-080000.zip", "OzanExtender-backup-20261001-080000.zip"],
            BackupArchive.SelectExpired(names, keep: 2));
    }

    [Fact]
    public void Runner_reports_partial_failures()
    {
        var store = NewStore("source");
        store.SaveSnippets(BvyParser.Parse(ParserTests.Fixture));
        var good = new FolderBackupTarget("Local", Path.Combine(_root, "local"));
        var bad = new FailingTarget();

        var result = BackupRunner.Run(store, [good, bad], keep: 5);

        Assert.False(result.Success);
        Assert.Contains("1 of 2", result.Summary);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "local")));
    }

    [Theory]
    [InlineData(null, null, true)] // never backed up
    [InlineData(-1.0, null, false)] // backed up an hour ago, nothing changed
    [InlineData(-25.0, null, true)] // interval passed
    [InlineData(-1.0, -0.5, true)] // changed 30 minutes ago, after the last backup
    public void Schedule_is_due_when_expected(double? lastRunHoursAgo, double? changedHoursAgo, bool expected)
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var state = new BackupState
        {
            LastRunUtc = lastRunHoursAgo is { } run ? now.AddHours(run) : null,
            LastChangeUtc = changedHoursAgo is { } changed ? now.AddHours(changed) : null,
        };

        Assert.Equal(expected, BackupSchedule.IsDue(new BackupSettings(), state, now));
    }

    [Fact]
    public void Recent_change_waits_for_the_quiet_period_and_disabled_never_runs()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var state = new BackupState { LastRunUtc = now.AddHours(-1), LastChangeUtc = now.AddSeconds(-30) };

        Assert.False(BackupSchedule.IsDue(new BackupSettings(), state, now));
        Assert.False(BackupSchedule.IsDue(new BackupSettings { Enabled = false }, new BackupState(), now));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FailingTarget : IBackupTarget
    {
        public string Name => "Broken";

        public void Upload(string zipPath, int keep) => throw new IOException("disk full");
    }
}
