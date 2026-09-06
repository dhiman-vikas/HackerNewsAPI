using RestfulAPIDemo.Api;

namespace RestfulAPIDemo.BestStories;

/// <summary>An immutable, score-ordered view of the best stories as of <see cref="FetchedAt"/>.</summary>
public sealed class BestStoriesSnapshot
{
    private readonly Story[] _stories;

    public BestStoriesSnapshot(Story[] stories, IReadOnlyDictionary<long, Story> byId, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(stories);
        ArgumentNullException.ThrowIfNull(byId);

        _stories = stories;
        ById = byId;
        FetchedAt = fetchedAt;
    }

    /// <summary>All stories, best first.</summary>
    public IReadOnlyList<Story> Stories => _stories;

    /// <summary>The same stories keyed by Hacker News id, used to reuse last-known-good copies when a fetch fails.</summary>
    public IReadOnlyDictionary<long, Story> ById { get; }

    /// <summary>Start of the refresh cycle that produced this snapshot (so the reported age is never under-stated).</summary>
    public DateTimeOffset FetchedAt { get; }

    public int Count => _stories.Length;

    /// <summary>The best <paramref name="n"/> stories, or all of them when fewer are available.</summary>
    public Story[] Top(int n) => _stories.AsSpan(0, Math.Clamp(n, 0, _stories.Length)).ToArray();
}
