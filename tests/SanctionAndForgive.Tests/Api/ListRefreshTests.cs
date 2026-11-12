using SanctionAndForgive.Api.Lists;

namespace SanctionAndForgive.Tests.Api;

// When the lists are refreshed (RefreshSchedule): scripted times and values of ListLoader.ChangesSql, with no clock and
// no database. Checks every 2 s, the full refresh every 15 minutes, as in production.
public sealed class ListRefreshTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private static RefreshSchedule Schedule() => new(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(2));

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    /// <summary>A refresh asked for and finished at once, as it went.</summary>
    private static bool Run(RefreshSchedule schedule, double seconds, string? changes, bool succeeds)
    {
        if (!schedule.Begin(At(seconds), changes))
        {
            return false;
        }

        schedule.Finished(At(seconds), succeeds);
        return true;
    }

    [Fact]
    public void TheFirstCheck_Refreshes_AndAnUnchangedValue_DoesntAgain()
    {
        var schedule = Schedule();

        Assert.True(Run(schedule, 0, "a", succeeds: true));
        Assert.False(schedule.Begin(At(2), "a"));
        Assert.False(schedule.Begin(At(4), "a"));
    }

    [Fact]
    public void ANewValue_RefreshesAtOnce()
    {
        var schedule = Schedule();
        Run(schedule, 0, "a", succeeds: true);

        Assert.True(schedule.Begin(At(2), "b"));
    }

    [Fact]
    public void AChangeMadeDuringARefresh_IsRefreshedForAtTheNextCheck()
    {
        var schedule = Schedule();
        Run(schedule, 0, "a", succeeds: true);

        // Read "b", refresh; the change "c" lands while it runs, so the next check reads "c".
        Assert.True(schedule.Begin(At(2), "b"));
        schedule.Finished(At(3), succeeded: true);

        Assert.True(schedule.Begin(At(5), "c"));
    }

    [Fact]
    public void AFailedRefresh_IsTriedAgainAfterItsWait_NotAtEveryCheck_UntilItWorks()
    {
        var schedule = Schedule();
        Run(schedule, 0, "a", succeeds: true);

        Assert.True(Run(schedule, 2, "b", succeeds: false));
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.RetryWait);
        Assert.False(schedule.Begin(At(4), "b"));
        Assert.False(schedule.Begin(At(6), "b"));

        Assert.True(Run(schedule, 7, "b", succeeds: false));
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.RetryWait);
        Assert.False(schedule.Begin(At(15), "b"));

        Assert.True(Run(schedule, 17, "b", succeeds: true));
        Assert.Equal(TimeSpan.Zero, schedule.RetryWait);
        Assert.False(schedule.Begin(At(19), "b"));
        Assert.False(schedule.Begin(At(40), "b"));
    }

    [Fact]
    public void ANewValue_DoesntWaitForAnEarlierFailuresRetry()
    {
        var schedule = Schedule();
        Run(schedule, 0, "a", succeeds: true);
        Run(schedule, 2, "b", succeeds: false);

        // The allow-list removal made on another replica moves the value: refreshed for at once.
        Assert.True(schedule.Begin(At(4), "c"));
    }

    [Fact]
    public void AFailedCheck_RefreshesOnlyForARetryOrTheBackstop()
    {
        var schedule = Schedule();
        Run(schedule, 0, "a", succeeds: true);

        Assert.False(schedule.Begin(At(2), changes: null));
        Assert.True(Run(schedule, 2, "b", succeeds: false));
        Assert.False(schedule.Begin(At(4), changes: null));
        Assert.True(schedule.Begin(At(7), changes: null));
    }

    [Fact]
    public void WithNothingChanging_TheBackstopRefreshesEvery15Minutes()
    {
        var schedule = Schedule();
        Run(schedule, 0, "a", succeeds: true);

        Assert.False(schedule.Begin(At(15 * 60 - 1), "a"));
        Assert.True(schedule.Begin(At(15 * 60), "a"));
    }

    [Fact]
    public void ItSleepsTheCheckInterval_OrLessWhenARetryFallsSooner()
    {
        var withCheck = Schedule();
        Run(withCheck, 0, "a", succeeds: true);
        Assert.Equal(TimeSpan.FromSeconds(2), withCheck.NextWait);
        Run(withCheck, 2, "b", succeeds: false);
        Assert.Equal(TimeSpan.FromSeconds(2), withCheck.NextWait);

        // The check off: only the backstop, and a retry when one is pending.
        var withoutCheck = new RefreshSchedule(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        Run(withoutCheck, 0, changes: null, succeeds: false);
        Assert.Equal(TimeSpan.FromSeconds(5), withoutCheck.NextWait);
        Assert.True(withoutCheck.Begin(At(5), changes: null));
    }

    [Fact]
    public void AFailedRefresh_IsTriedAgainAfter5s_ThenTwiceAsLong_AtMostAMinute()
    {
        var waits = new List<double>();
        var wait = TimeSpan.Zero;
        for (var i = 0; i < 7; i++)
        {
            wait = RefreshSchedule.NextRetryWait(wait);
            waits.Add(wait.TotalSeconds);
        }

        Assert.Equal([5, 10, 20, 40, 60, 60, 60], waits);
    }
}
