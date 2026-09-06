using System.Collections.Concurrent;
using RestfulAPIDemo.HackerNews;

namespace RestfulAPIDemo.Tests.Fakes;

/// <summary>Delegate-driven <see cref="IHackerNewsClient"/> for unit-testing the refresher and ranker without HTTP.</summary>
public sealed class FakeHackerNewsClient : IHackerNewsClient
{
    private int _listCalls;
    private int _itemCalls;
    private int _inFlight;
    private int _maxInFlight;

    public Func<CancellationToken, Task<long[]?>> ListSource { get; set; } = _ => Task.FromResult<long[]?>([]);

    public Func<long, CancellationToken, Task<ItemFetchResult>> ItemSource { get; set; } =
        (_, _) => Task.FromResult(ItemFetchResult.Missing("not configured"));

    public int ListCalls => Volatile.Read(ref _listCalls);

    public int ItemCalls => Volatile.Read(ref _itemCalls);

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public ConcurrentQueue<long> RequestedIds { get; } = new();

    /// <summary>Configures a list of plain stories with the given ids and scores.</summary>
    public void SetStories(params (long Id, int Score)[] stories)
    {
        var byId = stories.ToDictionary(s => s.Id, s => Item(s.Id, s.Score));
        ListSource = _ => Task.FromResult<long[]?>(stories.Select(s => s.Id).ToArray());
        ItemSource = (id, _) => Task.FromResult(byId.TryGetValue(id, out var item) ? ItemFetchResult.Found(item) : ItemFetchResult.Missing("null body"));
    }

    public static HackerNewsItem Item(
        long id,
        int? score = 100,
        string? title = null,
        string? url = null,
        string? by = null,
        long? time = 1_700_000_000,
        int? descendants = 10,
        string? type = "story",
        bool? deleted = null,
        bool? dead = null) =>
        new(id, type, by ?? $"user{id}", time, title ?? $"Story {id}", url ?? $"https://example.com/{id}", score, descendants, deleted, dead);

    public Task<long[]?> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listCalls);
        return ListSource(cancellationToken);
    }

    public async Task<ItemFetchResult> GetItemAsync(long id, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _itemCalls);
        RequestedIds.Enqueue(id);
        var inFlight = Interlocked.Increment(ref _inFlight);
        int observedMax;
        do
        {
            observedMax = Volatile.Read(ref _maxInFlight);
            if (inFlight <= observedMax)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _maxInFlight, inFlight, observedMax) != observedMax);

        try
        {
            return await ItemSource(id, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
