namespace RestfulAPIDemo.HackerNews;

/// <summary>Thin, non-throwing gateway to the two Hacker News endpoints this service uses.</summary>
public interface IHackerNewsClient
{
    /// <summary>
    /// Fetches <c>/v0/beststories.json</c>.
    /// Returns the ids in Hacker News' order, or <see langword="null"/> when the list could not be retrieved or parsed.
    /// Only the caller's own cancellation is surfaced as an exception.
    /// </summary>
    Task<long[]?> GetBestStoryIdsAsync(CancellationToken cancellationToken);

    /// <summary>Fetches <c>/v0/item/{id}.json</c>. Never throws; see <see cref="ItemFetchResult"/>.</summary>
    Task<ItemFetchResult> GetItemAsync(long id, CancellationToken cancellationToken);
}
