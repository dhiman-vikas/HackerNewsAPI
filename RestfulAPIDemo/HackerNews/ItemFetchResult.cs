namespace RestfulAPIDemo.HackerNews;

/// <summary>Outcome of fetching one Hacker News item.</summary>
public enum ItemStatus
{
    /// <summary>The item could not be fetched (transport error, timeout, 5xx after retries, malformed body). Default.</summary>
    Failed = 0,

    /// <summary>Hacker News answered authoritatively that there is no such item (literal <c>null</c> body or 404).</summary>
    Missing = 1,

    /// <summary>The item was fetched and parsed.</summary>
    Found = 2,
}

/// <summary>
/// Per-item result of <see cref="IHackerNewsClient.GetItemAsync"/>. The client never throws for expected
/// failures so that one bad item can never abort a whole refresh cycle.
/// <c>default(ItemFetchResult)</c> is a <see cref="ItemStatus.Failed"/> result, which makes a partially
/// filled result array safe to rank.
/// </summary>
public readonly record struct ItemFetchResult(ItemStatus Status, HackerNewsItem? Item, string? Reason)
{
    public static ItemFetchResult Found(HackerNewsItem item) => new(ItemStatus.Found, item, null);

    public static ItemFetchResult Missing(string reason) => new(ItemStatus.Missing, null, reason);

    public static ItemFetchResult Failed(string reason) => new(ItemStatus.Failed, null, reason);
}
