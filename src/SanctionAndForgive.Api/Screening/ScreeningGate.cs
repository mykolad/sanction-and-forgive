namespace SanctionAndForgive.Api.Screening;

/// <summary>Single screenings at once (<c>Screening:MaxConcurrent</c>, 4), waiting for one of those places at most
/// <c>Screening:MaxWaitSeconds</c> (2), and at most <c>Screening:MaxWaiting</c> (256) waiting at all, a bound on memory.
/// One that would wait longer, or one more, is refused (503 <c>busy</c>, <c>Retry-After: 1</c>): past its capacity a
/// replica answers quickly instead of making every caller wait longer and longer. The time, not the count, is the limit
/// in practice (owner's decision, 2026-11-12): a short pause doesn't refuse anyone, a replica that can't keep up
/// does.</summary>
public sealed class ScreeningGate : IDisposable
{
    private readonly SemaphoreSlim running;
    private readonly int limit;
    private readonly TimeSpan maxWait;
    private int admitted;

    public ScreeningGate(int maxConcurrent, int maxWaiting, TimeSpan maxWait)
    {
        var concurrent = Math.Max(1, maxConcurrent);
        running = new SemaphoreSlim(concurrent);
        limit = concurrent + Math.Max(0, maxWaiting);
        this.maxWait = maxWait < TimeSpan.Zero ? TimeSpan.Zero : maxWait;
    }

    /// <summary>Runs <paramref name="work"/> once a place is free; null when no place was free within the wait allowed,
    /// or at once when every place is taken and as many are waiting as allowed.</summary>
    public async Task<T?> TryRunAsync<T>(Func<T> work, CancellationToken cancellationToken)
        where T : class
    {
        if (Interlocked.Increment(ref admitted) > limit)
        {
            Interlocked.Decrement(ref admitted);
            return null;
        }

        try
        {
            if (!await running.WaitAsync(maxWait, cancellationToken))
            {
                return null;
            }

            try
            {
                return work();
            }
            finally
            {
                running.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref admitted);
        }
    }

    public void Dispose() => running.Dispose();
}
