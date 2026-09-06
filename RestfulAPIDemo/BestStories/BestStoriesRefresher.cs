using Microsoft.Extensions.Options;
using RestfulAPIDemo.HackerNews;

namespace RestfulAPIDemo.BestStories;

/// <summary>
/// The only component that calls Hacker News. Runs one refresh cycle, publishes the ranked snapshot to the
/// <see cref="SnapshotStore"/>, sleeps for <see cref="BestStoriesOptions.RefreshInterval"/>, repeats.
/// Because the delay is measured from the end of a cycle and <see cref="BestStoriesOptions.RefreshTimeout"/> is
/// shorter than the interval, cycles never overlap and upstream load is bounded by construction:
/// at most 1 + <see cref="BestStoriesOptions.MaxStories"/> requests per interval, regardless of client traffic.
/// </summary>
public sealed class BestStoriesRefresher(
    IHackerNewsClient client,
    SnapshotStore store,
    IOptions<BestStoriesOptions> options,
    TimeProvider timeProvider,
    ILogger<BestStoriesRefresher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed refresh must never stop the host; the previous snapshot keeps being served.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                store.FailCycle();
                Log.CycleCrashed(logger, exception);
            }

            var interval = options.Value.RefreshInterval;
            store.NextRefreshAt = timeProvider.GetUtcNow() + interval;
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Runs exactly one refresh cycle. Internal so tests can drive cycles deterministically.</summary>
    internal async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var startedAt = timeProvider.GetUtcNow();
        var startedTimestamp = timeProvider.GetTimestamp();
        store.NextRefreshAt = null;

        using var refreshTimeout = new CancellationTokenSource(settings.RefreshTimeout, timeProvider);
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshTimeout.Token);

        long[]? listedIds;
        try
        {
            listedIds = await client.GetBestStoryIdsAsync(cycle.Token);
        }
        catch (OperationCanceledException) when (refreshTimeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            listedIds = null;
        }

        if (listedIds is null)
        {
            store.FailCycle();
            Log.CycleFailed(logger, "beststories.json could not be retrieved", SnapshotAgeSeconds(startedAt));
            return;
        }

        var ids = Dedupe(listedIds, settings.MaxStories);
        var results = new ItemFetchResult[ids.Length]; // default(ItemFetchResult) == Failed, so an aborted fan-out is still safe to rank
        try
        {
            await Parallel.ForAsync(
                0,
                ids.Length,
                new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrentItemFetches, CancellationToken = cycle.Token },
                async (index, token) => results[index] = await client.GetItemAsync(ids[index], token));
        }
        catch (OperationCanceledException) when (refreshTimeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            Log.CycleTimedOut(logger, settings.RefreshTimeout.TotalSeconds);
        }

        var rank = BestStoriesRanker.Build(ids, results, store.Current, startedAt, settings.MinCoveragePercent);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var (id, reason) in rank.ExcludedItems)
            {
                Log.ItemExcluded(logger, id, reason);
            }
        }

        if (rank.FailedIds.Count > 0 && logger.IsEnabled(LogLevel.Warning))
        {
            Log.ItemsFailed(logger, rank.FailedIds.Count, string.Join(", ", rank.FailedIds));
        }

        if (rank.Snapshot is null)
        {
            store.FailCycle();
            Log.CycleRejectedByCoverage(logger, ids.Length - rank.Unresolved, ids.Length, settings.MinCoveragePercent, SnapshotAgeSeconds(startedAt));
            return;
        }

        store.Publish(rank.Snapshot);
        var elapsedMs = (long)timeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        Log.CycleCompleted(logger, listedIds.Length, ids.Length, rank.Snapshot.Count, rank.Excluded, rank.Reused, rank.Unresolved, elapsedMs);
    }

    /// <summary>First occurrence wins, order is preserved, and the result is capped so the fan-out bound is a property of the code.</summary>
    internal static long[] Dedupe(long[] ids, int max)
    {
        var capacity = Math.Min(ids.Length, max);
        var seen = new HashSet<long>(capacity);
        var unique = new List<long>(capacity);
        foreach (var id in ids)
        {
            if (unique.Count >= max)
            {
                break;
            }

            if (seen.Add(id))
            {
                unique.Add(id);
            }
        }

        return [.. unique];
    }

    private long? SnapshotAgeSeconds(DateTimeOffset now) =>
        store.Current is { } snapshot ? (long)(now - snapshot.FetchedAt).TotalSeconds : null;
}
