using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using RestfulAPIDemo.BestStories;
using RestfulAPIDemo.HackerNews;

namespace RestfulAPIDemo.Tests.Fakes;

/// <summary>
/// Hosts the real application in-process with Hacker News replaced by <see cref="FakeHackerNewsHandler"/>,
/// the clock replaced by <see cref="FakeTimeProvider"/> and all logs captured.
/// Configure <see cref="Handler"/> and <see cref="Settings"/> BEFORE the first call to <c>CreateClient()</c>
/// or <c>Services</c>: the host (and its first refresh cycle) starts on first use.
/// </summary>
public sealed class BestStoriesApiFactory : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public FakeHackerNewsHandler Handler { get; } = new();

    public FakeTimeProvider Time { get; } = new(StartTime);

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>"Production" by default so the tests exercise the real error shapes (no developer exception page).</summary>
    public string EnvironmentName { get; set; } = Environments.Production;

    /// <summary>Set to <see langword="false"/> to run the resilience pipeline and refresher on the real clock.</summary>
    public bool UseFakeTime { get; set; } = true;

    /// <summary>
    /// Configuration overrides. Defaults: a one-hour refresh interval (only the warm-up cycle runs by itself; tests
    /// drive further cycles via <see cref="Refresher"/>), no retry delay, and a single retry.
    /// </summary>
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["BestStories:RefreshInterval"] = "01:00:00",
        ["BestStories:RefreshTimeout"] = "00:59:00",
        ["BestStories:ColdStartWait"] = "00:00:10",
        ["HackerNews:RetryBaseDelay"] = "00:00:00",
        ["HackerNews:MaxRetryAttempts"] = "1",
    };

    public SnapshotStore Store => Services.GetRequiredService<SnapshotStore>();

    public BestStoriesRefresher Refresher => Services.GetRequiredService<BestStoriesRefresher>();

    /// <summary>Starts the host if necessary and waits (real clock) until the first refresh cycle has finished.</summary>
    public async Task<BestStoriesSnapshot?> WarmAsync(TimeSpan? timeout = null)
    {
        var store = Store;
        if (store.CompletedCycles > 0)
        {
            return store.Current;
        }

        var cycle = store.WaitForCycleAsync();
        if (store.CompletedCycles > 0)
        {
            return store.Current;
        }

        return await cycle.WaitAsync(timeout ?? TimeSpan.FromSeconds(15));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            if (UseFakeTime)
            {
                services.AddSingleton<TimeProvider>(Time);
            }

            services.AddSingleton<ILoggerProvider>(Logs);

            // Later registration for the same named client wins; the resilience handler configured by the app remains.
            services.AddHttpClient(HackerNewsClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
        });
    }
}
