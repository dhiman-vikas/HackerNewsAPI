using RestfulAPIDemo.Api;
using RestfulAPIDemo.HackerNews;

namespace RestfulAPIDemo.BestStories;

/// <summary>Pure ranking step: turns one cycle's fetch results into a sorted snapshot (or rejects the cycle).</summary>
internal static class BestStoriesRanker
{
    /// <param name="ids">De-duplicated ids in Hacker News' list order.</param>
    /// <param name="results">One result per id; never-fetched slots are <c>default</c>, i.e. <see cref="ItemStatus.Failed"/>.</param>
    /// <param name="previous">The snapshot currently being served, used as last-known-good for failed fetches.</param>
    /// <param name="fetchedAt">Start of the cycle.</param>
    /// <param name="minCoveragePercent">Minimum percentage of ids that must be resolved for the cycle to publish.</param>
    public static RankResult Build(long[] ids, ItemFetchResult[] results, BestStoriesSnapshot? previous, DateTimeOffset fetchedAt, int minCoveragePercent)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Length != ids.Length)
        {
            throw new ArgumentException("One result per id is required.", nameof(results));
        }

        var ranked = new List<RankedStory>(ids.Length);
        var excluded = new List<ExcludedItem>();
        var failedIds = new List<long>();
        var reused = 0;
        var unresolved = 0;

        for (var position = 0; position < ids.Length; position++)
        {
            var id = ids[position];
            var result = results[position];
            switch (result.Status)
            {
                case ItemStatus.Found:
                    if (StoryMapper.TryMap(result.Item!, out var story, out var reason))
                    {
                        ranked.Add(new RankedStory(id, position, story));
                    }
                    else
                    {
                        excluded.Add(new ExcludedItem(id, reason));
                    }

                    break;

                case ItemStatus.Missing:
                    excluded.Add(new ExcludedItem(id, result.Reason ?? "missing"));
                    break;

                default:
                    failedIds.Add(id);
                    if (previous is not null && previous.ById.TryGetValue(id, out var lastKnownGood))
                    {
                        ranked.Add(new RankedStory(id, position, lastKnownGood));
                        reused++;
                    }
                    else
                    {
                        unresolved++;
                    }

                    break;
            }
        }

        // Coverage gate in integer arithmetic: 1 unresolved of 10 ids passes at 90 % exactly.
        if (unresolved * 100 > ids.Length * (100 - minCoveragePercent))
        {
            return new RankResult(null, excluded, reused, unresolved, failedIds);
        }

        // Score descending, then Hacker News' own list position ascending: deterministic and matches HN for ties.
        ranked.Sort(static (a, b) => a.Story.Score != b.Story.Score
            ? b.Story.Score.CompareTo(a.Story.Score)
            : a.Position.CompareTo(b.Position));

        var stories = new Story[ranked.Count];
        var byId = new Dictionary<long, Story>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            stories[i] = ranked[i].Story;
            byId[ranked[i].Id] = ranked[i].Story;
        }

        return new RankResult(new BestStoriesSnapshot(stories, byId, fetchedAt), excluded, reused, unresolved, failedIds);
    }

    private readonly record struct RankedStory(long Id, int Position, Story Story);
}

/// <summary>An id that was answered by Hacker News but is not a servable story.</summary>
internal readonly record struct ExcludedItem(long Id, string Reason);

/// <summary>
/// Outcome of ranking one cycle. <see cref="Snapshot"/> is <see langword="null"/> when the coverage gate rejected the cycle.
/// </summary>
internal sealed record RankResult(
    BestStoriesSnapshot? Snapshot,
    IReadOnlyList<ExcludedItem> ExcludedItems,
    int Reused,
    int Unresolved,
    IReadOnlyList<long> FailedIds)
{
    public int Excluded => ExcludedItems.Count;
}
