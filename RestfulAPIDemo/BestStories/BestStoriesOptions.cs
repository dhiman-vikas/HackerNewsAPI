using System.ComponentModel.DataAnnotations;

namespace RestfulAPIDemo.BestStories;

/// <summary>Settings for the best-stories snapshot and its refresh loop (section <c>BestStories</c>).</summary>
public sealed class BestStoriesOptions
{
    public const string SectionName = "BestStories";

    /// <summary>Pause between the end of one refresh cycle and the start of the next. Also the response max-age.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound on one cycle's duration; must be less than <see cref="RefreshInterval"/> so cycles never overlap.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(50);

    /// <summary>Maximum ids fetched per cycle and the upper bound for <c>n</c>. Hacker News documents 500 as its list cap.</summary>
    [Range(1, 500)]
    public int MaxStories { get; set; } = 500;

    /// <summary>Maximum number of item requests in flight to Hacker News at any moment.</summary>
    [Range(1, 64)]
    public int MaxConcurrentItemFetches { get; set; } = 8;

    /// <summary>How long a request waits for the very first snapshot before answering 503.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan ColdStartWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A cycle is published only if at least this percentage of listed ids was resolved (fetched, excluded, or
    /// reused from the previous snapshot). Protects callers from a silently truncated "best" list.
    /// </summary>
    [Range(0, 100)]
    public int MinCoveragePercent { get; set; } = 90;

    /// <summary>Snapshot age after which the readiness check reports Degraded.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(5);
}
