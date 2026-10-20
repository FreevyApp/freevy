using Freevy.Core;

namespace Freevy.Core.Tests;

public class StatsTests
{
    [Fact]
    public void Records_expansions_per_day_and_in_total()
    {
        var day = new DateOnly(2026, 10, 20);

        var stats = new UsageStats()
            .Record(day, typedLength: 4, insertedLength: 40)
            .Record(day, typedLength: 4, insertedLength: 4)
            .Record(day.AddDays(1), typedLength: 3, insertedLength: 10);

        Assert.Equal(new DayStats(day, 2, 36), stats.For(day));
        Assert.Equal(new DayStats(day.AddDays(1), 1, 7), stats.For(day.AddDays(1)));
        Assert.Equal(3, stats.TotalExpansions);
        Assert.Equal(43, stats.TotalKeystrokesSaved);
    }

    [Fact]
    public void Never_counts_negative_savings_and_forgets_old_days()
    {
        var day = new DateOnly(2026, 10, 20);
        var stats = new UsageStats().Record(day.AddDays(-UsageStats.DaysKept - 5), 2, 50);

        stats = stats.Record(day, typedLength: 10, insertedLength: 3);

        Assert.Equal(0, stats.For(day).KeystrokesSaved);
        Assert.Single(stats.Days);
        Assert.Equal(48, stats.TotalKeystrokesSaved);
    }
}
