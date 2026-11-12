using SanctionAndForgive.Api.Lists;

namespace SanctionAndForgive.Tests.Api;

// A refresh that failed is tried again after a wait that grows: never at every check, never more than a minute apart.
public sealed class ListRefreshTests
{
    [Fact]
    public void AFailedRefresh_IsTriedAgainAfter5s_ThenTwiceAsLong_AtMostAMinute()
    {
        var waits = new List<double>();
        var wait = TimeSpan.Zero;
        for (var i = 0; i < 7; i++)
        {
            wait = ListRefreshService.RetryWait(wait);
            waits.Add(wait.TotalSeconds);
        }

        Assert.Equal([5, 10, 20, 40, 60, 60, 60], waits);
    }
}
