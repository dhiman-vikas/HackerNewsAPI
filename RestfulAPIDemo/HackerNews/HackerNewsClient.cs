using System.Net;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace RestfulAPIDemo.HackerNews;

/// <summary>
/// <see cref="IHackerNewsClient"/> over the named <see cref="HttpClient"/> registered by
/// <see cref="HackerNewsServiceCollectionExtensions.AddHackerNewsClient"/>, which carries the resilience pipeline
/// (timeouts, retries with jitter, circuit breaker). This class only maps HTTP outcomes to results.
/// </summary>
public sealed class HackerNewsClient(IHttpClientFactory httpClientFactory, ILogger<HackerNewsClient> logger) : IHackerNewsClient
{
    public const string HttpClientName = "HackerNews";

    private const string BestStoriesPath = "beststories.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<long[]?> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.GetAsync(BestStoriesPath, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Log.ListRequestFailed(logger, BestStoriesPath, FormattableString.Invariant($"HTTP {(int)response.StatusCode}"));
                return null;
            }

            var ids = await ReadJsonAsync<long[]>(response, cancellationToken);
            if (ids is null)
            {
                Log.ListRequestFailed(logger, BestStoriesPath, "body was null");
            }

            return ids;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            Log.ListRequestFailed(logger, BestStoriesPath, Describe(exception));
            return null;
        }
    }

    public async Task<ItemFetchResult> GetItemAsync(long id, CancellationToken cancellationToken)
    {
        var path = FormattableString.Invariant($"item/{id}.json");
        var client = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ItemFetchResult.Missing("HTTP 404");
            }

            if (!response.IsSuccessStatusCode)
            {
                var reason = FormattableString.Invariant($"HTTP {(int)response.StatusCode}");
                Log.ItemRequestFailed(logger, path, reason);
                return ItemFetchResult.Failed(reason);
            }

            var item = await ReadJsonAsync<HackerNewsItem>(response, cancellationToken);
            return item is null ? ItemFetchResult.Missing("null body") : ItemFetchResult.Found(item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ItemFetchResult.Failed("cancelled");
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            var reason = Describe(exception);
            Log.ItemRequestFailed(logger, path, reason);
            return ItemFetchResult.Failed(reason);
        }
    }

    /// <summary>Deserialises the body regardless of the advertised content type (a misbehaving proxy may label JSON as text).</summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static bool IsExpectedFailure(Exception exception) => exception is
        HttpRequestException          // DNS, connection, TLS failures
        or TimeoutRejectedException   // resilience pipeline: total timeout exhausted
        or BrokenCircuitException     // resilience pipeline: circuit open, failing fast
        or OperationCanceledException // attempt timeout surfaced as cancellation by the transport
        or JsonException              // HTML error page, truncated body, unexpected shape
        or IOException;               // connection dropped mid-body

    private static string Describe(Exception exception) =>
        exception is TimeoutRejectedException or OperationCanceledException ? "timed out" : exception.GetType().Name;
}
