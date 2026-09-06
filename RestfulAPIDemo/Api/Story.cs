using System.Text.Json.Serialization;

namespace RestfulAPIDemo.Api;

/// <summary>
/// A Hacker News story as returned by this API. The property names and their order are the public contract,
/// so they are pinned with attributes rather than inherited from the global naming policy.
/// </summary>
/// <param name="Title">The story title, exactly as Hacker News returns it.</param>
/// <param name="Uri">The linked URL; <see langword="null"/> for text-only posts (e.g. "Ask HN").</param>
/// <param name="PostedBy">The Hacker News username of the submitter.</param>
/// <param name="Time">Submission time in UTC, serialised as ISO-8601 with an explicit "+00:00" offset.</param>
/// <param name="Score">The story's current score.</param>
/// <param name="CommentCount">Total number of comments (Hacker News' <c>descendants</c>); 0 when absent.</param>
public sealed record Story(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("postedBy")] string? PostedBy,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("commentCount")] int CommentCount);
