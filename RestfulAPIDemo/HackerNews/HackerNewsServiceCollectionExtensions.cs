using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace RestfulAPIDemo.HackerNews;

public static class HackerNewsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHackerNewsClient"/> backed by a named <see cref="HttpClient"/> with the standard
    /// resilience pipeline: total timeout -> retry (exponential back-off with jitter) -> circuit breaker -> attempt timeout.
    /// </summary>
    public static IServiceCollection AddHackerNewsClient(this IServiceCollection services)
    {
        services.AddOptions<HackerNewsOptions>()
            .BindConfiguration(HackerNewsOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(o => o.TotalTimeout > o.AttemptTimeout, "HackerNews:TotalTimeout must be greater than HackerNews:AttemptTimeout.")
            .Validate(o => o.BaseAddress.EndsWith('/'), "HackerNews:BaseAddress must end with '/' so relative paths resolve under it.")
            .ValidateOnStart();

        services.AddHttpClient(HackerNewsClient.HttpClientName, static (serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseAddress, UriKind.Absolute);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("RestfulAPIDemo-BestStories/1.0 (+https://github.com/HackerNews/API)");
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5), // pick up DNS changes
                AutomaticDecompression = DecompressionMethods.All,
            })
            .AddStandardResilienceHandler()
            .Configure(static (resilience, serviceProvider) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<HackerNewsOptions>>().Value;

                resilience.AttemptTimeout.Timeout = options.AttemptTimeout;
                resilience.TotalRequestTimeout.Timeout = options.TotalTimeout;

                resilience.Retry.MaxRetryAttempts = options.MaxRetryAttempts;
                resilience.Retry.Delay = options.RetryBaseDelay;
                resilience.Retry.BackoffType = DelayBackoffType.Exponential;
                resilience.Retry.UseJitter = true;

                // Polly requires SamplingDuration >= 2 x AttemptTimeout.
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, 2 * options.AttemptTimeout.Ticks));
                resilience.CircuitBreaker.MinimumThroughput = 20;
                resilience.CircuitBreaker.FailureRatio = 0.5;
                resilience.CircuitBreaker.BreakDuration = options.BreakDuration;
            });

        services.AddSingleton<IHackerNewsClient, HackerNewsClient>();
        return services;
    }
}
