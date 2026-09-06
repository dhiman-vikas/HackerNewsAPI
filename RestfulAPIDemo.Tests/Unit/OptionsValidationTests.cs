using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RestfulAPIDemo.BestStories;
using RestfulAPIDemo.HackerNews;
using RestfulAPIDemo.Tests.Fakes;

namespace RestfulAPIDemo.Tests.Unit;

/// <summary>
/// Proves that every documented configuration bound is enforced at host start (ValidateOnStart), that the
/// cross-property rules carry their explanatory messages, and that valid overrides bind and take effect.
/// </summary>
public sealed class OptionsValidationTests
{
    [Fact]
    public void RefreshInterval_zero_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:RefreshInterval"] = "00:00:00";

        var exception = AssertStartupFails(factory);

        Assert.Contains("RefreshInterval", exception.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(BestStoriesOptions), exception.OptionsType);
    }

    [Fact]
    public void RefreshInterval_above_one_day_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:RefreshInterval"] = "1.00:00:01";

        var exception = AssertStartupFails(factory);

        Assert.Contains("RefreshInterval", exception.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(BestStoriesOptions), exception.OptionsType);
    }

    [Fact]
    public void RefreshTimeout_not_less_than_interval_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:RefreshInterval"] = "00:01:00";
        factory.Settings["BestStories:RefreshTimeout"] = "00:01:00";

        var exception = AssertStartupFails(factory);

        Assert.Contains("RefreshTimeout must be less than", exception.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(BestStoriesOptions), exception.OptionsType);
    }

    [Fact]
    public void MaxStories_zero_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:MaxStories"] = "0";

        var exception = AssertStartupFails(factory);

        Assert.Contains("MaxStories", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxStories_501_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:MaxStories"] = "501";

        var exception = AssertStartupFails(factory);

        Assert.Contains("MaxStories", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxConcurrentItemFetches_zero_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:MaxConcurrentItemFetches"] = "0";

        var exception = AssertStartupFails(factory);

        Assert.Contains("MaxConcurrentItemFetches", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxConcurrentItemFetches_65_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:MaxConcurrentItemFetches"] = "65";

        var exception = AssertStartupFails(factory);

        Assert.Contains("MaxConcurrentItemFetches", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColdStartWait_too_small_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:ColdStartWait"] = "00:00:00.050";

        var exception = AssertStartupFails(factory);

        Assert.Contains("ColdStartWait", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MinCoveragePercent_101_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:MinCoveragePercent"] = "101";

        var exception = AssertStartupFails(factory);

        Assert.Contains("MinCoveragePercent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HackerNews_TotalTimeout_not_greater_than_attempt_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["HackerNews:AttemptTimeout"] = "00:00:10";
        factory.Settings["HackerNews:TotalTimeout"] = "00:00:10";

        var exception = AssertStartupFails(factory);

        Assert.Contains("TotalTimeout must be greater", exception.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(HackerNewsOptions), exception.OptionsType);
    }

    [Fact]
    public void HackerNews_MaxRetryAttempts_zero_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["HackerNews:MaxRetryAttempts"] = "0";

        var exception = AssertStartupFails(factory);

        Assert.Contains("MaxRetryAttempts", exception.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(HackerNewsOptions), exception.OptionsType);
    }

    [Fact]
    public void HackerNews_BreakDuration_too_small_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["HackerNews:BreakDuration"] = "00:00:00.100";

        var exception = AssertStartupFails(factory);

        Assert.Contains("BreakDuration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HackerNews_BaseAddress_without_trailing_slash_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["HackerNews:BaseAddress"] = "https://example.com/v0";

        var exception = AssertStartupFails(factory);

        Assert.Contains("BaseAddress must end with '/'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HackerNews_BaseAddress_not_a_url_fails_startup()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["HackerNews:BaseAddress"] = "not a url";

        var exception = AssertStartupFails(factory);

        Assert.Contains("BaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_overrides_start_and_bind()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:MaxStories"] = "3";
        factory.Settings["BestStories:RefreshInterval"] = "00:00:05";
        factory.Settings["BestStories:RefreshTimeout"] = "00:00:04";
        factory.Handler.SetStories((1, 10));
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<BestStoriesOptions>>().Value;
        Assert.Equal(3, options.MaxStories);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(4), options.RefreshTimeout);

        using var response = await client.GetAsync("/api/stories/best?n=4");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = Assert.Single(problem.GetProperty("errors").GetProperty("n").EnumerateArray());
        Assert.Contains("between 1 and 3", error.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_match_appsettings()
    {
        using var factory = new BestStoriesApiFactory();

        var hackerNews = factory.Services.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
        var bestStories = factory.Services.GetRequiredService<IOptions<BestStoriesOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(10), hackerNews.AttemptTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), hackerNews.TotalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), hackerNews.BreakDuration);
        Assert.Equal(1, hackerNews.MaxRetryAttempts); // factory override, not the appsettings value of 2
        Assert.Equal(TimeSpan.Zero, hackerNews.RetryBaseDelay); // factory override
        Assert.Equal("https://hacker-news.firebaseio.com/v0/", hackerNews.BaseAddress);

        Assert.Equal(500, bestStories.MaxStories);
        Assert.Equal(8, bestStories.MaxConcurrentItemFetches);
        Assert.Equal(90, bestStories.MinCoveragePercent);
        Assert.Equal(TimeSpan.FromMinutes(5), bestStories.StaleAfter);
        Assert.Equal(TimeSpan.FromHours(1), bestStories.RefreshInterval); // factory override
    }

    [Fact]
    public async Task Environment_variable_style_override_works()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Settings["BestStories:MaxConcurrentItemFetches"] = "2";
        factory.Handler.SetStories(Enumerable.Range(1, 20).Select(i => ((long)i, i * 10)).ToArray());

        var snapshot = await factory.WarmAsync();

        Assert.Equal(2, factory.Services.GetRequiredService<IOptions<BestStoriesOptions>>().Value.MaxConcurrentItemFetches);
        Assert.NotNull(snapshot);
        Assert.Equal(20, snapshot.Count);
        Assert.Equal(20, factory.Handler.ItemCalls);
        Assert.InRange(factory.Handler.MaxInFlight, 1, 2);
    }

    /// <summary>
    /// Starting the host runs every ValidateOnStart validator. A failing <see cref="BestStoriesOptions"/> surfaces
    /// as a bare <see cref="OptionsValidationException"/>; a failing <see cref="HackerNewsOptions"/> arrives inside an
    /// <see cref="AggregateException"/> because the standard resilience handler's own options validator re-reads
    /// <c>IOptions&lt;HackerNewsOptions&gt;.Value</c> and fails a second time. Either way the host never starts.
    /// </summary>
    private static OptionsValidationException AssertStartupFails(BestStoriesApiFactory factory)
    {
        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = Flatten(thrown).OfType<OptionsValidationException>().FirstOrDefault();
        Assert.True(validation is not null, $"Expected an OptionsValidationException in the chain but got: {thrown}");
        return validation;
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;

        var inner = exception is AggregateException aggregate ? aggregate.InnerExceptions : (IEnumerable<Exception?>)[exception.InnerException];
        foreach (var child in inner)
        {
            if (child is null)
            {
                continue;
            }

            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
