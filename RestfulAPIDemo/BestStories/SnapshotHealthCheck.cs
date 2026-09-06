using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace RestfulAPIDemo.BestStories;

/// <summary>
/// Readiness: Unhealthy until the first snapshot exists, Degraded once the snapshot is older than
/// <see cref="BestStoriesOptions.StaleAfter"/> (Hacker News has been unreachable for a while), Healthy otherwise.
/// </summary>
public sealed class SnapshotHealthCheck(SnapshotStore store, IOptions<BestStoriesOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = store.Current;
        if (snapshot is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("No Hacker News snapshot has been loaded yet."));
        }

        var age = timeProvider.GetUtcNow() - snapshot.FetchedAt;
        var data = new Dictionary<string, object>
        {
            ["snapshotAgeSeconds"] = (long)age.TotalSeconds,
            ["storyCount"] = snapshot.Count,
            ["fetchedAt"] = snapshot.FetchedAt,
        };

        return Task.FromResult(age > options.Value.StaleAfter
            ? HealthCheckResult.Degraded("The snapshot is stale; Hacker News may be unreachable.", data: data)
            : HealthCheckResult.Healthy("Serving a fresh snapshot.", data));
    }
}
