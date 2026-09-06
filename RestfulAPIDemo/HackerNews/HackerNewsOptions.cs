using System.ComponentModel.DataAnnotations;

namespace RestfulAPIDemo.HackerNews;

/// <summary>Settings for the outbound Hacker News HTTP client (section <c>HackerNews</c>).</summary>
public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    /// <summary>Base address of the Hacker News API; must end with a slash.</summary>
    [Required, Url]
    public string BaseAddress { get; set; } = "https://hacker-news.firebaseio.com/v0/";

    /// <summary>Timeout for a single HTTP attempt.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout for a request including all retries; must exceed <see cref="AttemptTimeout"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:00.200", "00:10:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Number of retries after the first attempt for transient failures (5xx, 408, 429, transport errors, attempt timeouts).</summary>
    [Range(1, 10)]
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Base delay of the exponential back-off between retries (jitter is always applied). Zero disables waiting.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:00:10", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the circuit stays open after Hacker News is judged unhealthy.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
