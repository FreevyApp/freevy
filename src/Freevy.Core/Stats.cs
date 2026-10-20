namespace Freevy.Core;

/// <summary>Expansions and keystrokes saved on one day.</summary>
public sealed record DayStats(DateOnly Date, int Expansions, int KeystrokesSaved);

/// <summary>Usage statistics kept in stats.json; only counts, never what was typed.</summary>
public sealed record UsageStats
{
    public const int DaysKept = 90;

    public List<DayStats> Days { get; init; } = [];

    public int TotalExpansions { get; init; }

    public long TotalKeystrokesSaved { get; init; }

    /// <summary>
    /// Records one expansion. Saved keystrokes are the characters Freevy typed minus the
    /// characters the user typed to trigger it (never negative).
    /// </summary>
    public UsageStats Record(DateOnly day, int typedLength, int insertedLength)
    {
        var saved = Math.Max(0, insertedLength - typedLength);
        var days = Days.Where(d => d.Date > day.AddDays(-DaysKept)).ToList();
        var index = days.FindIndex(d => d.Date == day);
        if (index >= 0)
        {
            days[index] = days[index] with { Expansions = days[index].Expansions + 1, KeystrokesSaved = days[index].KeystrokesSaved + saved };
        }
        else
        {
            days.Add(new DayStats(day, 1, saved));
        }

        return this with
        {
            Days = days,
            TotalExpansions = TotalExpansions + 1,
            TotalKeystrokesSaved = TotalKeystrokesSaved + saved,
        };
    }

    public DayStats For(DateOnly day) => Days.FirstOrDefault(d => d.Date == day) ?? new DayStats(day, 0, 0);
}
