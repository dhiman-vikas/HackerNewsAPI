using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RestfulAPIDemo.HackerNews;
using RestfulAPIDemo.Tests.Fakes;

namespace RestfulAPIDemo.Tests.Unit;

/// <summary>
/// Proves the real <c>AddHackerNewsClient()</c> registration (standard resilience pipeline: total timeout, retry,
/// circuit breaker, attempt timeout) behaves as configured, on the real clock, with the Hacker News server faked.
/// </summary>
public sealed class ResiliencePipelineTests
{
    private const string BrokenCircuit = "BrokenCircuitException";

    /// <summary>Sub-second settings so timeouts and the breaker can be observed without waiting for the defaults.</summary>
    private static Dictionary<string, string?> FastSettings() => new()
    {
        ["HackerNews:AttemptTimeout"] = "00:00:00.200",
        ["HackerNews:TotalTimeout"] = "00:00:02",
        ["HackerNews:RetryBaseDelay"] = "00:00:00.010",
        ["HackerNews:BreakDuration"] = "00:00:00.500",
        ["HackerNews:MaxRetryAttempts"] = "2",
    };

    /// <summary>
    /// Builds a container exactly like production does for the Hacker News client (real options, real named
    /// <see cref="HttpClient"/>, real resilience handler), swapping only the primary handler for the fake server.
    /// </summary>
    private static ServiceProvider BuildProvider(FakeHackerNewsHandler handler, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHackerNewsClient();
        services.AddHttpClient(HackerNewsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Transient_500_then_200_succeeds_after_two_attempts()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.Items[1] = HnFixtures.Story(1);
        // ItemCalls is incremented before the responder runs, so the first call sees 1.
        handler.ItemResponder = (_, _) => handler.ItemCalls == 1 ? FakeHackerNewsHandler.Status(HttpStatusCode.InternalServerError) : null;
        await using var provider = BuildProvider(handler, FastSettings());
        var client = provider.GetRequiredService<IHackerNewsClient>();

        var result = await client.GetItemAsync(1, CancellationToken.None);

        Assert.Equal(ItemStatus.Found, result.Status);
        Assert.Equal(1, result.Item?.Id);
        Assert.Equal(2, handler.ItemCalls);
    }

    [Fact]
    public async Task Persistent_500_exhausts_after_three_attempts()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.ItemResponder = (_, _) => FakeHackerNewsHandler.Status(HttpStatusCode.InternalServerError);
        await using var provider = BuildProvider(handler, FastSettings());
        var client = provider.GetRequiredService<IHackerNewsClient>();

        var result = await client.GetItemAsync(1, CancellationToken.None);

        Assert.Equal(ItemStatus.Failed, result.Status);
        Assert.Equal("HTTP 500", result.Reason);
        Assert.Equal(3, handler.ItemCalls); // 1 attempt + MaxRetryAttempts (2)
    }

    [Fact]
    public async Task Item_404_is_not_retried()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.ItemResponder = (_, _) => FakeHackerNewsHandler.Status(HttpStatusCode.NotFound);
        await using var provider = BuildProvider(handler, FastSettings());
        var client = provider.GetRequiredService<IHackerNewsClient>();

        var result = await client.GetItemAsync(1, CancellationToken.None);

        Assert.Equal(ItemStatus.Missing, result.Status);
        Assert.Equal("HTTP 404", result.Reason);
        Assert.Equal(1, handler.ItemCalls);
    }

    [Fact]
    public async Task Null_body_is_not_retried()
    {
        using var handler = new FakeHackerNewsHandler(); // no entry for id 1 -> literal null body
        await using var provider = BuildProvider(handler, FastSettings());
        var client = provider.GetRequiredService<IHackerNewsClient>();

        var result = await client.GetItemAsync(1, CancellationToken.None);

        Assert.Equal(ItemStatus.Missing, result.Status);
        Assert.Equal("null body", result.Reason);
        Assert.Equal(1, handler.ItemCalls);
    }

    [Fact]
    public async Task Hanging_handler_times_out_within_total_timeout()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.Items[1] = HnFixtures.Story(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ItemGate = gate; // never released: every attempt hangs until the pipeline cancels it
        await using var provider = BuildProvider(handler, FastSettings());
        var client = provider.GetRequiredService<IHackerNewsClient>();

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await client.GetItemAsync(1, CancellationToken.None);
            stopwatch.Stop();

            Assert.Equal(ItemStatus.Failed, result.Status);
            Assert.Equal("timed out", result.Reason);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5)); // ~3 x 200 ms attempts, 2 s total cap
            Assert.Equal(3, handler.ItemCalls); // each 200 ms attempt timeout is a retryable failure
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Slow_attempt_is_retried_then_succeeds()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.Items[1] = HnFixtures.Story(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ItemGate = gate; // the first attempt parks here until the 200 ms attempt timeout cancels it
        await using var provider = BuildProvider(handler, FastSettings());
        var client = provider.GetRequiredService<IHackerNewsClient>();

        try
        {
            var pending = client.GetItemAsync(1, CancellationToken.None);
            await FakeTimeExtensions.WaitUntilAsync(() => handler.ItemCalls >= 1, what: "the first attempt to reach the handler");
            handler.ItemGate = null; // subsequent attempts answer immediately

            var result = await pending;

            Assert.Equal(ItemStatus.Found, result.Status);
            Assert.Equal(1, result.Item?.Id);
            Assert.InRange(handler.ItemCalls, 2, 3); // the slow attempt was abandoned and a later one succeeded
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Breaker_opens_after_enough_failures_and_fails_fast()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.ItemResponder = (_, _) => FakeHackerNewsHandler.Status(HttpStatusCode.InternalServerError);
        var settings = FastSettings();
        settings["HackerNews:BreakDuration"] = "00:00:10"; // stay open for the whole test: no half-open probe can sneak in
        await using var provider = BuildProvider(handler, settings);
        var client = provider.GetRequiredService<IHackerNewsClient>();

        var results = new List<ItemFetchResult>(30);
        var openedAtCall = -1;
        var handlerCallsWhenOpened = -1;
        var afterOpen = new Stopwatch();
        for (var i = 0; i < 30; i++)
        {
            var result = await client.GetItemAsync(i + 1, CancellationToken.None);
            results.Add(result);
            if (openedAtCall < 0 && result.Reason == BrokenCircuit)
            {
                openedAtCall = i;
                handlerCallsWhenOpened = handler.ItemCalls;
                afterOpen.Start();
            }
        }

        afterOpen.Stop();

        Assert.All(results, r => Assert.Equal(ItemStatus.Failed, r.Status));
        Assert.True(openedAtCall >= 0, "the circuit never opened");
        Assert.All(results.Take(openedAtCall), r => Assert.Equal("HTTP 500", r.Reason)); // exhausted retries before the break
        Assert.All(results.Skip(openedAtCall), r => Assert.Equal(BrokenCircuit, r.Reason)); // failing fast after it
        Assert.InRange(handlerCallsWhenOpened, 20, 89); // MinimumThroughput (20) samples are needed before it can open
        Assert.Equal(handlerCallsWhenOpened, handler.ItemCalls); // no upstream traffic while open
        Assert.True(handler.ItemCalls < 90, $"{handler.ItemCalls} handler calls: 30 x 3 attempts means the breaker never engaged");
        Assert.InRange(afterOpen.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1)); // ~23 fail-fast calls, no retry back-off
    }

    [Fact]
    public async Task Breaker_half_opens_after_break_duration()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.Items[1] = HnFixtures.Story(1);
        handler.ItemResponder = (_, _) => FakeHackerNewsHandler.Status(HttpStatusCode.InternalServerError);
        await using var provider = BuildProvider(handler, FastSettings()); // BreakDuration 500 ms
        var client = provider.GetRequiredService<IHackerNewsClient>();

        var opened = false;
        for (var i = 0; i < 30 && !opened; i++)
        {
            var failure = await client.GetItemAsync(1, CancellationToken.None);
            opened = failure.Reason == BrokenCircuit;
        }

        Assert.True(opened, "the circuit never opened");
        var callsWhileOpen = handler.ItemCalls;

        await Task.Delay(TimeSpan.FromMilliseconds(600)); // real clock: let BreakDuration elapse
        handler.ItemResponder = null; // upstream has recovered

        var probe = await client.GetItemAsync(1, CancellationToken.None);
        var afterClose = await client.GetItemAsync(1, CancellationToken.None);

        Assert.Equal(ItemStatus.Found, probe.Status); // the half-open probe went through and succeeded
        Assert.Equal(ItemStatus.Found, afterClose.Status); // and closed the circuit again
        Assert.Equal(callsWhileOpen + 2, handler.ItemCalls);
    }

    [Fact]
    public async Task Configured_production_defaults_pass_pipeline_validation()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.Items[HnFixtures.SpecId] = HnFixtures.SpecItemJson;
        await using var provider = BuildProvider(handler, []); // 10 s / 30 s / 2 / 300 ms / 30 s
        var client = provider.GetRequiredService<IHackerNewsClient>();

        var result = await client.GetItemAsync(HnFixtures.SpecId, CancellationToken.None); // first use builds and validates the pipeline

        Assert.Equal(ItemStatus.Found, result.Status);
        Assert.Equal(HnFixtures.SpecItem, result.Item);
        Assert.Equal(1, handler.ItemCalls);
    }

    [Theory]
    [InlineData("00:00:05", "00:00:05")]
    [InlineData("00:00:10", "00:00:05")]
    public void Total_timeout_must_exceed_attempt_timeout_is_enforced_by_options(string attemptTimeout, string totalTimeout)
    {
        using var handler = new FakeHackerNewsHandler();
        var settings = FastSettings();
        settings["HackerNews:AttemptTimeout"] = attemptTimeout;
        settings["HackerNews:TotalTimeout"] = totalTimeout;
        using var provider = BuildProvider(handler, settings);
        var options = provider.GetRequiredService<IOptions<HackerNewsOptions>>();

        var exception = Assert.Throws<OptionsValidationException>(() => options.Value);

        Assert.Contains("HackerNews:TotalTimeout must be greater than HackerNews:AttemptTimeout", exception.Message, StringComparison.Ordinal);
    }
}
