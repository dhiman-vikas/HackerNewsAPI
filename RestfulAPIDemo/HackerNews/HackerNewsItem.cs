namespace RestfulAPIDemo.HackerNews;

/// <summary>
/// The subset of a Hacker News <c>/v0/item/{id}.json</c> document this service reads.
/// Every field except <see cref="Id"/> is optional upstream; unknown fields (kids, text, parts, ...) are ignored.
/// </summary>
public sealed record HackerNewsItem(
    long Id,
    string? Type,
    string? By,
    long? Time,
    string? Title,
    string? Url,
    int? Score,
    int? Descendants,
    bool? Deleted,
    bool? Dead);
