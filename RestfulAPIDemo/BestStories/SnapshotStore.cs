namespace RestfulAPIDemo.BestStories;

/// <summary>
/// Hands the latest <see cref="BestStoriesSnapshot"/> from the single writer (the refresher) to any number of
/// readers (requests) without locks, and lets cold-start readers await the cycle in progress.
/// </summary>
public sealed class SnapshotStore
{
    private BestStoriesSnapshot? _current;
    private TaskCompletionSource<BestStoriesSnapshot?> _cycle = NewCycle();
    private long _nextRefreshAtTicks;
    private int _completedCycles;

    /// <summary>The latest published snapshot, or <see langword="null"/> before the first successful cycle.</summary>
    public BestStoriesSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>Number of cycles that have finished (successfully or not).</summary>
    public int CompletedCycles => Volatile.Read(ref _completedCycles);

    /// <summary>
    /// When the next refresh cycle is scheduled to start, or <see langword="null"/> while a cycle is running.
    /// Used for the <c>Retry-After</c> header.
    /// </summary>
    public DateTimeOffset? NextRefreshAt
    {
        get
        {
            var ticks = Volatile.Read(ref _nextRefreshAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        set => Volatile.Write(ref _nextRefreshAtTicks, value?.UtcTicks ?? 0);
    }

    /// <summary>
    /// A task that completes when the cycle currently in progress finishes: with the new snapshot on success,
    /// or <see langword="null"/> on failure. Every caller during one cycle shares the same task.
    /// </summary>
    public Task<BestStoriesSnapshot?> WaitForCycleAsync() => Volatile.Read(ref _cycle).Task;

    public void Publish(BestStoriesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
        CompleteCycle(snapshot);
    }

    public void FailCycle() => CompleteCycle(null);

    private void CompleteCycle(BestStoriesSnapshot? result)
    {
        // Swap first so that a reader arriving after this point waits for the *next* cycle, then release the waiters.
        var finished = Interlocked.Exchange(ref _cycle, NewCycle());
        Interlocked.Increment(ref _completedCycles);
        finished.TrySetResult(result);
    }

    // RunContinuationsAsynchronously: thousands of parked requests must resume on the thread pool, not inline on the refresher.
    private static TaskCompletionSource<BestStoriesSnapshot?> NewCycle() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
