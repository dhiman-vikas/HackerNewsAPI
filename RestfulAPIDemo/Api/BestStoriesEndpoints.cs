using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using RestfulAPIDemo.BestStories;

namespace RestfulAPIDemo.Api;

/// <summary>The single public endpoint: <c>GET /api/stories/best?n=...</c>.</summary>
public static class BestStoriesEndpoints
{
    public const string Route = "/api/stories/best";
    public const string EndpointName = "GetBestStories";

    /// <summary>Seconds a client should wait before retrying while the very first refresh cycle is still running.</summary>
    private const long RetryAfterWhileRefreshing = 5;

    public static RouteHandlerBuilder MapBestStories(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Route, GetBestStoriesAsync)
            .WithName(EndpointName)
            .WithTags("Stories")
            .WithSummary("Get the best Hacker News stories")
            .WithDescription("Returns the best n stories from Hacker News (/v0/beststories.json) in descending order of score. "
                + "Fewer than n items are returned when fewer stories are currently available.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<Results<Ok<Story[]>, ValidationProblem, ProblemHttpResult>> GetBestStoriesAsync(
        HttpContext httpContext,
        SnapshotStore store,
        IOptions<BestStoriesOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!NParser.TryParse(httpContext.Request.Query["n"], settings.MaxStories, out var n, out var error))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["n"] = [error] });
        }

        // Hot path: a lock-free read of the latest published snapshot. Only a cold start ever waits.
        var snapshot = store.Current ?? await WaitForFirstSnapshotAsync(store, settings.ColdStartWait, timeProvider, cancellationToken);
        if (snapshot is null)
        {
            return ServiceUnavailable(httpContext, store, settings, timeProvider);
        }

        var age = Math.Max(0L, (long)Math.Floor((timeProvider.GetUtcNow() - snapshot.FetchedAt).TotalSeconds));
        var ttl = (long)settings.RefreshInterval.TotalSeconds;
        httpContext.Response.Headers.CacheControl = string.Create(CultureInfo.InvariantCulture, $"public, max-age={Math.Clamp(ttl - age, 0, ttl)}");
        httpContext.Response.Headers.Age = age.ToString(CultureInfo.InvariantCulture);

        return TypedResults.Ok(snapshot.Top(n));
    }

    private static async Task<BestStoriesSnapshot?> WaitForFirstSnapshotAsync(
        SnapshotStore store, TimeSpan maxWait, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // Grab the in-progress cycle first, then re-check: a publish that lands between the two reads is never missed.
        var cycle = store.WaitForCycleAsync();
        if (store.Current is { } published)
        {
            return published;
        }

        try
        {
            return await cycle.WaitAsync(maxWait, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            return store.Current;
        }
    }

    private static ProblemHttpResult ServiceUnavailable(
        HttpContext httpContext, SnapshotStore store, BestStoriesOptions settings, TimeProvider timeProvider)
    {
        var maxRetryAfter = Math.Max(1L, (long)settings.RefreshInterval.TotalSeconds);
        var retryAfter = store.NextRefreshAt is { } next
            ? Math.Clamp((long)Math.Ceiling((next - timeProvider.GetUtcNow()).TotalSeconds), 1L, maxRetryAfter)
            : RetryAfterWhileRefreshing;

        httpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        httpContext.Response.Headers.CacheControl = "no-store";

        return TypedResults.Problem(
            title: "Best stories are temporarily unavailable",
            detail: "Hacker News data has not been loaded yet. Retry shortly.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
