using RestfulAPIDemo.Api;
using RestfulAPIDemo.BestStories;

namespace RestfulAPIDemo.Tests.Unit;

/// <summary>Proves the lock-free SnapshotStore hand-off: Publish/FailCycle semantics, one shared waiter task per cycle, asynchronous continuations and NextRefreshAt round-tripping.</summary>
public sealed class SnapshotStoreTests
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static BestStoriesSnapshot NewSnapshot() => new([], new Dictionary<long, Story>(), FetchedAt);

    [Fact]
    public void Current_is_null_until_first_publish()
    {
        var store = new SnapshotStore();

        Assert.Null(store.Current);

        var snapshot = NewSnapshot();
        store.Publish(snapshot);

        Assert.Same(snapshot, store.Current);
    }

    [Fact]
    public void CompletedCycles_starts_at_zero()
    {
        var store = new SnapshotStore();

        Assert.Equal(0, store.CompletedCycles);
    }

    [Fact]
    public async Task Publish_sets_current_and_completes_current_waiters_with_snapshot()
    {
        var store = new SnapshotStore();
        var first = store.WaitForCycleAsync();
        var second = store.WaitForCycleAsync();
        var snapshot = NewSnapshot();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        store.Publish(snapshot);

        Assert.Same(snapshot, store.Current);
        Assert.Same(snapshot, await first);
        Assert.Same(snapshot, await second);
        Assert.Equal(1, store.CompletedCycles);
    }

    [Fact]
    public async Task FailCycle_completes_current_waiters_with_null_and_keeps_current()
    {
        var store = new SnapshotStore();
        var snapshot = NewSnapshot();
        store.Publish(snapshot);
        var waiter = store.WaitForCycleAsync();
        Assert.False(waiter.IsCompleted);

        store.FailCycle();

        Assert.Null(await waiter);
        Assert.Same(snapshot, store.Current);
        Assert.Equal(2, store.CompletedCycles);
    }

    [Fact]
    public async Task FailCycle_before_any_publish_leaves_current_null_but_counts_the_cycle()
    {
        var store = new SnapshotStore();
        var waiter = store.WaitForCycleAsync();

        store.FailCycle();

        Assert.Null(await waiter);
        Assert.Null(store.Current);
        Assert.Equal(1, store.CompletedCycles);
    }

    [Fact]
    public async Task Waiter_arriving_after_completion_waits_for_next_cycle()
    {
        var store = new SnapshotStore();
        var first = NewSnapshot();
        store.Publish(first);

        var waiter = store.WaitForCycleAsync();
        Assert.False(waiter.IsCompleted);

        var second = NewSnapshot();
        store.Publish(second);

        Assert.Same(second, await waiter);
        Assert.Same(second, store.Current);
        Assert.Equal(2, store.CompletedCycles);
    }

    [Fact]
    public async Task Thousand_waiters_share_one_task_and_get_same_instance()
    {
        var store = new SnapshotStore();
        var waiters = Enumerable.Range(0, 1000).Select(_ => store.WaitForCycleAsync()).ToArray();
        var snapshot = NewSnapshot();
        Assert.All(waiters, waiter => Assert.Same(waiters[0], waiter));

        store.Publish(snapshot);
        var results = await Task.WhenAll(waiters);

        Assert.Equal(1000, results.Length);
        Assert.All(results, result => Assert.Same(snapshot, result));
    }

    [Fact]
    public async Task Continuations_run_asynchronously()
    {
        // A continuation that asks to run synchronously and then blocks: if the store ran it inline inside
        // Publish (i.e. without RunContinuationsAsynchronously), Publish itself would block until the gate opens,
        // and the gate only opens after Publish has been observed to return.
        var store = new SnapshotStore();
        using var gate = new ManualResetEventSlim(false);
        var continuationStarted = 0;
        var continuation = store.WaitForCycleAsync().ContinueWith(
            _ =>
            {
                Interlocked.Exchange(ref continuationStarted, 1);
                gate.Wait();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var snapshot = NewSnapshot();

        var publish = Task.Run(() => store.Publish(snapshot));
        var finishedFirst = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(5)));
        gate.Set();

        Assert.Same(publish, finishedFirst);
        await publish;
        await continuation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref continuationStarted));
        Assert.Same(snapshot, store.Current);
    }

    [Fact]
    public void NextRefreshAt_round_trips_and_null_means_in_progress()
    {
        var store = new SnapshotStore();
        Assert.Null(store.NextRefreshAt);

        var scheduled = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.FromHours(5));
        store.NextRefreshAt = scheduled;
        var readBack = store.NextRefreshAt;

        Assert.NotNull(readBack);
        Assert.Equal(scheduled.UtcTicks, readBack.Value.UtcTicks);
        Assert.Equal(TimeSpan.Zero, readBack.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 7, 30, 0, TimeSpan.Zero), readBack.Value);

        store.NextRefreshAt = null;

        Assert.Null(store.NextRefreshAt);
    }

    [Fact]
    public void Publish_null_throws_ArgumentNullException()
    {
        var store = new SnapshotStore();
        var waiter = store.WaitForCycleAsync();

        var exception = Assert.Throws<ArgumentNullException>(() => store.Publish(null!));

        Assert.Equal("snapshot", exception.ParamName);
        Assert.Null(store.Current);
        Assert.Equal(0, store.CompletedCycles);
        Assert.False(waiter.IsCompleted);
    }

    [Fact]
    public async Task Concurrent_publishes_and_waiters_never_lose_a_completion()
    {
        var store = new SnapshotStore();
        using var start = new ManualResetEventSlim(false);

        Task<Task<BestStoriesSnapshot?>[]> Worker() => Task.Run(() =>
        {
            var snapshot = NewSnapshot();
            var waiters = new Task<BestStoriesSnapshot?>[100];
            start.Wait();
            for (var i = 0; i < waiters.Length; i++)
            {
                waiters[i] = store.WaitForCycleAsync();
                store.Publish(snapshot);
            }

            return waiters;
        });

        var left = Worker();
        var right = Worker();
        start.Set();
        var waiters = (await left).Concat(await right).ToArray();

        var results = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, results.Length);
        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(200, store.CompletedCycles);
        Assert.NotNull(store.Current);
    }
}
