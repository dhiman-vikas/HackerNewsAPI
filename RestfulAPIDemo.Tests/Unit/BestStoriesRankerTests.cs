using RestfulAPIDemo.Api;
using RestfulAPIDemo.BestStories;
using RestfulAPIDemo.HackerNews;
using RestfulAPIDemo.Tests.Fakes;

namespace RestfulAPIDemo.Tests.Unit;

/// <summary>Proves that <see cref="BestStoriesRanker.Build"/> sorts, excludes, reuses and coverage-gates one cycle's fetch results exactly as specified, without HTTP or time.</summary>
public sealed class BestStoriesRankerTests
{
    private static readonly DateTimeOffset CycleStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sorts_by_score_descending()
    {
        long[] ids = [1, 2, 3];
        ItemFetchResult[] results = [Found(1, 10), Found(2, 50), Found(3, 30)];

        var result = Build(ids, results);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal([50, 30, 10], snapshot.Stories.Select(s => s.Score).ToArray());
        Assert.Equal(["Story 2", "Story 3", "Story 1"], Titles(snapshot));
    }

    [Fact]
    public void Ties_keep_hn_list_position()
    {
        // Odd ids score 50, even ids score 20: within each score band the output must follow the list order.
        var seenOrders = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 0; seed < 50; seed++)
        {
            long[] ids = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            new Random(seed).Shuffle(ids);
            Assert.True(seenOrders.Add(string.Join(",", ids)), $"seed {seed} repeated an earlier id order");
            var results = ids.Select(id => Found(id, id % 2 == 1 ? 50 : 20)).ToArray();

            var result = Build(ids, results);

            var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
            var expected = ids.Where(id => id % 2 == 1).Concat(ids.Where(id => id % 2 == 0)).Select(Title).ToArray();
            Assert.Equal(expected, Titles(snapshot));
        }

        Assert.Equal(50, seenOrders.Count);
    }

    [Fact]
    public void Missing_items_are_excluded_and_do_not_count()
    {
        long[] ids = [1, 2, 3, 4];
        ItemFetchResult[] results = [Found(1), ItemFetchResult.Missing("HTTP 404"), Deleted(3), Found(4)];

        var result = Build(ids, results);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(["Story 1", "Story 4"], Titles(snapshot));
        Assert.Equal(2, result.Excluded);
        Assert.Equal([2, 3], result.ExcludedItems.Select(e => e.Id).ToArray());
        Assert.Contains(result.ExcludedItems, e => e.Reason == "deleted");
        Assert.Contains(result.ExcludedItems, e => e.Reason == "HTTP 404");
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(0, result.Reused);
        Assert.Empty(result.FailedIds);
    }

    [Fact]
    public void Missing_item_is_not_reused_from_previous()
    {
        var previous = Snapshot((7, 99));
        long[] ids = [7, 8];
        ItemFetchResult[] results = [ItemFetchResult.Missing("null body"), Found(8, 10)];

        var result = Build(ids, results, previous);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.False(snapshot.ById.ContainsKey(7));
        Assert.Single(snapshot.Stories);
        Assert.Equal(0, result.Reused);
        Assert.Single(result.ExcludedItems, e => e.Id == 7 && e.Reason == "null body");
    }

    [Fact]
    public void Failed_item_reuses_previous_copy()
    {
        var previous = Snapshot((7, 99));
        long[] ids = [7, 8];
        ItemFetchResult[] results = [ItemFetchResult.Failed("HTTP 500"), Found(8, 10)];

        var result = Build(ids, results, previous);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(99, snapshot.Stories[0].Score);
        Assert.Same(previous.ById[7], snapshot.Stories[0]);
        Assert.Same(previous.ById[7], snapshot.ById[7]);
        Assert.Equal(1, result.Reused);
        Assert.Equal(0, result.Unresolved);
        Assert.Contains(7L, result.FailedIds);
        Assert.Single(result.FailedIds);
        Assert.Empty(result.ExcludedItems);
    }

    [Fact]
    public void Found_item_uses_fresh_copy_not_previous()
    {
        var previous = Snapshot((7, 99));
        long[] ids = [7];
        ItemFetchResult[] results = [Found(7, 5)];

        var result = Build(ids, results, previous);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(5, snapshot.ById[7].Score);
        Assert.NotSame(previous.ById[7], snapshot.ById[7]);
        Assert.Equal(0, result.Reused);
    }

    [Fact]
    public void Failed_item_without_previous_is_unresolved()
    {
        var ids = Ids(10);
        var results = ids.Select(id => Found(id)).ToArray();
        results[4] = ItemFetchResult.Failed("timeout");

        var result = Build(ids, results, previous: null, minCoveragePercent: 90);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(9, snapshot.Count);
        Assert.False(snapshot.ById.ContainsKey(5));
        Assert.Equal(1, result.Unresolved);
        Assert.Equal(0, result.Reused);
        Assert.Equal([5], result.FailedIds.ToArray());
        Assert.Empty(result.ExcludedItems);
    }

    [Fact]
    public void Failed_item_absent_from_previous_is_unresolved()
    {
        var previous = Snapshot((1, 10), (2, 20));
        var ids = Ids(10);
        var results = ids.Select(id => Found(id)).ToArray();
        results[9] = ItemFetchResult.Failed("HTTP 503");

        var result = Build(ids, results, previous, minCoveragePercent: 90);

        Assert.NotNull(result.Snapshot);
        Assert.Equal(1, result.Unresolved);
        Assert.Equal(0, result.Reused);
        Assert.Equal([10], result.FailedIds.ToArray());
    }

    [Fact]
    public void Coverage_gate_rejects_when_unresolved_exceeds_allowance()
    {
        var ids = Ids(10);
        var results = ids.Select(id => Found(id)).ToArray();
        results[0] = ItemFetchResult.Failed("HTTP 500");
        results[1] = ItemFetchResult.Failed("HTTP 500");

        var result = Build(ids, results, previous: null, minCoveragePercent: 90);

        Assert.Null(result.Snapshot);
        Assert.Equal(2, result.Unresolved);
        Assert.Equal(0, result.Reused);
        Assert.Equal([1, 2], result.FailedIds.ToArray());
        Assert.Empty(result.ExcludedItems);
    }

    [Fact]
    public void Coverage_gate_publishes_when_unresolved_equals_allowance()
    {
        // Integer arithmetic edge: 1 unresolved of 10 is exactly 90 % coverage and must pass.
        var ids = Ids(10);
        var results = ids.Select(id => Found(id)).ToArray();
        results[0] = ItemFetchResult.Failed("HTTP 500");

        var result = Build(ids, results, previous: null, minCoveragePercent: 90);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(9, snapshot.Count);
        Assert.Equal(1, result.Unresolved);
    }

    [Fact]
    public void Coverage_gate_100_percent_rejects_any_unresolved()
    {
        var ids = Ids(10);
        var results = ids.Select(id => Found(id)).ToArray();
        results[9] = ItemFetchResult.Failed("HTTP 500");

        var result = Build(ids, results, previous: null, minCoveragePercent: 100);

        Assert.Null(result.Snapshot);
        Assert.Equal(1, result.Unresolved);
    }

    [Fact]
    public void Coverage_gate_0_percent_never_rejects()
    {
        var ids = Ids(10);
        var results = ids.Select(_ => ItemFetchResult.Failed("HTTP 500")).ToArray();

        var result = Build(ids, results, previous: null, minCoveragePercent: 0);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Empty(snapshot.Stories);
        Assert.Equal(10, result.Unresolved);
        Assert.Equal(ids, result.FailedIds.ToArray());
    }

    [Fact]
    public void Legitimate_exclusions_do_not_trip_coverage_gate()
    {
        // 5 of 10 items are deleted: that is 50 % excluded, 0 % unresolved, so even a 100 % gate publishes.
        var ids = Ids(10);
        var results = ids.Select(id => id % 2 == 0 ? Deleted(id) : Found(id)).ToArray();

        var result = Build(ids, results, previous: null, minCoveragePercent: 100);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(5, snapshot.Count);
        Assert.Equal(["Story 1", "Story 3", "Story 5", "Story 7", "Story 9"], Titles(snapshot));
        Assert.Equal(5, result.Excluded);
        Assert.All(result.ExcludedItems, e => Assert.Equal("deleted", e.Reason));
        Assert.Equal(0, result.Unresolved);
        Assert.Empty(result.FailedIds);
    }

    [Fact]
    public void Ids_that_left_the_list_are_not_resurrected()
    {
        var previous = Snapshot((99, 500), (1, 10));
        long[] ids = [1, 2];
        ItemFetchResult[] results = [ItemFetchResult.Failed("HTTP 500"), Found(2, 20)];

        var result = Build(ids, results, previous);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(2, snapshot.Count);
        Assert.False(snapshot.ById.ContainsKey(99));
        Assert.DoesNotContain(snapshot.Stories, s => s.Title == "Story 99");
        Assert.DoesNotContain(snapshot.Stories, s => s.Score == 500);
        Assert.True(snapshot.ById.ContainsKey(1));
        Assert.True(snapshot.ById.ContainsKey(2));
        Assert.Equal(1, result.Reused);
    }

    [Fact]
    public void Empty_id_list_yields_empty_snapshot()
    {
        var result = Build([], [], previous: null, minCoveragePercent: 100);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(0, snapshot.Count);
        Assert.Empty(snapshot.Stories);
        Assert.Empty(snapshot.ById);
        Assert.Equal(CycleStart, snapshot.FetchedAt);
        Assert.Equal(0, result.Excluded);
        Assert.Equal(0, result.Reused);
        Assert.Equal(0, result.Unresolved);
        Assert.Empty(result.FailedIds);
    }

    [Fact]
    public void Unfetched_default_results_are_unresolved_without_previous()
    {
        long[] ids = [1, 2, 3];
        var results = new ItemFetchResult[ids.Length];

        var result = Build(ids, results, previous: null, minCoveragePercent: 90);

        Assert.Null(result.Snapshot);
        Assert.Equal(3, result.Unresolved);
        Assert.Equal(0, result.Reused);
        Assert.Equal(ids, result.FailedIds.ToArray());
        Assert.Empty(result.ExcludedItems);
    }

    [Fact]
    public void Unfetched_default_results_reuse_previous_copies()
    {
        var previous = Snapshot((1, 10), (2, 20), (3, 30));
        long[] ids = [1, 2, 3];
        var results = new ItemFetchResult[ids.Length];

        var result = Build(ids, results, previous, minCoveragePercent: 90);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(3, result.Reused);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(ids, result.FailedIds.ToArray());
        Assert.Equal([30, 20, 10], snapshot.Stories.Select(s => s.Score).ToArray());
        foreach (var id in ids)
        {
            Assert.Same(previous.ById[id], snapshot.ById[id]);
        }
    }

    [Fact]
    public void ById_contains_every_published_story_once()
    {
        var ids = Ids(5);
        var results = ids.Select(id => Found(id, (int)(id * 7 % 5))).ToArray();

        var result = Build(ids, results);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(snapshot.Count, snapshot.ById.Count);
        Assert.Equal(ids.Order().ToArray(), snapshot.ById.Keys.Order().ToArray());
        foreach (var story in snapshot.Stories)
        {
            Assert.Single(snapshot.ById.Values, v => ReferenceEquals(v, story));
        }

        foreach (var id in ids)
        {
            Assert.Equal(Title(id), snapshot.ById[id].Title);
        }
    }

    [Fact]
    public void Top_n_returns_min_n_count()
    {
        var ids = Ids(5);
        var results = ids.Select(id => Found(id, (int)id * 10)).ToArray();

        var snapshot = Assert.IsType<BestStoriesSnapshot>(Build(ids, results).Snapshot);

        Assert.Equal(5, snapshot.Count);
        Assert.Equal(2, snapshot.Top(2).Length);
        Assert.Equal(snapshot.Stories.Take(2).ToArray(), snapshot.Top(2));
        Assert.Equal(snapshot.Count, snapshot.Top(100).Length);
        Assert.Equal(snapshot.Stories.ToArray(), snapshot.Top(100));
        Assert.Empty(snapshot.Top(0));
    }

    [Fact]
    public void FetchedAt_is_the_value_passed_in()
    {
        var fetchedAt = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2));
        long[] ids = [1];
        ItemFetchResult[] results = [Found(1)];

        var result = BestStoriesRanker.Build(ids, results, previous: null, fetchedAt, minCoveragePercent: 90);

        var snapshot = Assert.IsType<BestStoriesSnapshot>(result.Snapshot);
        Assert.Equal(fetchedAt, snapshot.FetchedAt);
        Assert.Equal(fetchedAt.Offset, snapshot.FetchedAt.Offset);
    }

    [Fact]
    public void Throws_when_results_length_differs_from_ids()
    {
        long[] ids = [1, 2];

        var shorter = Assert.Throws<ArgumentException>(() => Build(ids, [Found(1)]));
        var longer = Assert.Throws<ArgumentException>(() => Build(ids, [Found(1), Found(2), Found(3)]));

        Assert.Equal("results", shorter.ParamName);
        Assert.Equal("results", longer.ParamName);
    }

    [Fact]
    public void Throws_when_ids_or_results_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => BestStoriesRanker.Build(null!, [], null, CycleStart, 90));
        Assert.Throws<ArgumentNullException>(() => BestStoriesRanker.Build([], null!, null, CycleStart, 90));
    }

    private static RankResult Build(long[] ids, ItemFetchResult[] results, BestStoriesSnapshot? previous = null, int minCoveragePercent = 90) =>
        BestStoriesRanker.Build(ids, results, previous, CycleStart, minCoveragePercent);

    private static long[] Ids(int count) => Enumerable.Range(1, count).Select(i => (long)i).ToArray();

    private static string Title(long id) => $"Story {id}";

    private static string[] Titles(BestStoriesSnapshot snapshot) => snapshot.Stories.Select(s => s.Title ?? "<null>").ToArray();

    private static ItemFetchResult Found(long id, int score = 100) => ItemFetchResult.Found(FakeHackerNewsClient.Item(id, score));

    private static ItemFetchResult Deleted(long id) => ItemFetchResult.Found(FakeHackerNewsClient.Item(id, deleted: true));

    /// <summary>A previously published snapshot holding the given stories (order irrelevant for reuse).</summary>
    private static BestStoriesSnapshot Snapshot(params (long Id, int Score)[] stories)
    {
        var byId = new Dictionary<long, Story>();
        foreach (var (id, score) in stories)
        {
            byId[id] = new Story(Title(id), $"https://example.com/{id}", $"user{id}", DateTimeOffset.FromUnixTimeSeconds(1_600_000_000), score, 3);
        }

        var ordered = byId.Values.OrderByDescending(s => s.Score).ToArray();
        return new BestStoriesSnapshot(ordered, byId, CycleStart.AddHours(-1));
    }
}
