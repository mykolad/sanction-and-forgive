namespace SanctionAndForgive.Api.Screening;

/// <summary>Single screenings at once (<c>Screening:MaxConcurrent</c>, 4) and waiting for one of those places
/// (<c>Screening:MaxWaiting</c>, 32). One more is refused at once (503 <c>busy</c>, <c>Retry-After: 1</c>) rather than
/// queued: past its capacity a replica answers quickly instead of making every caller wait for seconds.</summary>
public sealed class ScreeningGate : IDisposable
{
    private readonly SemaphoreSlim running;
    private readonly int limit;
    private int admitted;

    public ScreeningGate(int maxConcurrent, int maxWaiting)
    {
        var concurrent = Math.Max(1, maxConcurrent);
        running = new SemaphoreSlim(concurrent);
        limit = concurrent + Math.Max(0, maxWaiting);
    }

    /// <summary>Runs <paramref name="work"/> once a place is free; null, at once, when every place is taken and as
    /// many are waiting as allowed.</summary>
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
            await running.WaitAsync(cancellationToken);
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
