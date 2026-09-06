using System.Diagnostics.CodeAnalysis;
using RestfulAPIDemo.Api;
using RestfulAPIDemo.HackerNews;

namespace RestfulAPIDemo.BestStories;

/// <summary>Maps a raw Hacker News item to the API's <see cref="Story"/>, or explains why it is excluded.</summary>
internal static class StoryMapper
{
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>
    /// Values are passed through untouched (no trimming, HTML decoding or URL normalisation); absent optional
    /// fields become <see langword="null"/> (strings) or 0 (numbers). Deleted, dead and time-less items are excluded
    /// because the contract's <c>time</c> is mandatory and such items are not stories a reader can visit.
    /// </summary>
    public static bool TryMap(HackerNewsItem item, [NotNullWhen(true)] out Story? story, [NotNullWhen(false)] out string? exclusionReason)
    {
        ArgumentNullException.ThrowIfNull(item);

        story = null;
        exclusionReason = item switch
        {
            { Deleted: true } => "deleted",
            { Dead: true } => "dead",
            { Time: null } => "no time",
            { Time: var t } when t < MinUnixSeconds || t > MaxUnixSeconds => "time out of range",
            _ => null,
        };

        if (exclusionReason is not null)
        {
            return false;
        }

        story = new Story(
            Title: item.Title,
            Uri: item.Url,
            PostedBy: item.By,
            Time: DateTimeOffset.FromUnixTimeSeconds(item.Time!.Value),
            Score: item.Score ?? 0,
            CommentCount: item.Descendants ?? 0);
        return true;
    }
}
