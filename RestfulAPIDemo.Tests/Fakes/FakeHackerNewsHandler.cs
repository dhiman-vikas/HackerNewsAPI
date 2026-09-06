using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RestfulAPIDemo.Tests.Fakes;

/// <summary>
/// In-memory stand-in for hacker-news.firebaseio.com, installed as the primary handler of the "HackerNews"
/// <see cref="HttpClient"/> so the production resilience pipeline stays in the chain. Records every request and
/// tracks the maximum number of concurrent in-flight requests.
/// </summary>
public sealed partial class FakeHackerNewsHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private int _inFlight;
    private int _maxInFlight;
    private int _listCalls;
    private int _itemCalls;

    /// <summary>Ids served by <c>/v0/beststories.json</c> unless <see cref="ListResponder"/> is set.</summary>
    public List<long> Ids { get; } = [];

    /// <summary>Raw JSON bodies served by <c>/v0/item/{id}.json</c>; ids without an entry answer the literal <c>null</c>.</summary>
    public ConcurrentDictionary<long, string> Items { get; } = new();

    /// <summary>Overrides the list response; return <see langword="null"/> to fall back to <see cref="Ids"/>.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? ListResponder { get; set; }

    /// <summary>Overrides an item response; return <see langword="null"/> to fall back to <see cref="Items"/>.</summary>
    public Func<long, HttpRequestMessage, HttpResponseMessage?>? ItemResponder { get; set; }

    /// <summary>When set, list requests wait for this to complete before answering (parks the refresh cycle).</summary>
    public TaskCompletionSource? ListGate { get; set; }

    /// <summary>When set, item requests wait for this to complete before answering.</summary>
    public TaskCompletionSource? ItemGate { get; set; }

    /// <summary>Artificial latency added to every request (real clock).</summary>
    public TimeSpan Latency { get; set; }

    public int ListCalls => Volatile.Read(ref _listCalls);

    public int ItemCalls => Volatile.Read(ref _itemCalls);

    public int TotalCalls => ListCalls + ItemCalls;

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public IReadOnlyCollection<RecordedRequest> Requests => _requests;

    /// <summary>Replaces the list and items with plain stories of the given ids and scores.</summary>
    public void SetStories(params (long Id, int Score)[] stories)
    {
        Ids.Clear();
        Items.Clear();
        foreach (var (id, score) in stories)
        {
            Ids.Add(id);
            Items[id] = HnFixtures.Story(id, score);
        }
    }

    /// <summary>Adds an id to the list and (optionally) its body. Omit the body to have the id answer <c>null</c>.</summary>
    public void Add(long id, string? body)
    {
        Ids.Add(id);
        if (body is not null)
        {
            Items[id] = body;
        }
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    public static HttpResponseMessage Html(string body = HnFixtures.HtmlErrorPage, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        int observedMax;
        do
        {
            observedMax = Volatile.Read(ref _maxInFlight);
            if (inFlight <= observedMax)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _maxInFlight, inFlight, observedMax) != observedMax);

        try
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            _requests.Enqueue(new RecordedRequest(request.Method, path, request.Headers.Accept.ToString(), request.Headers.UserAgent.ToString()));

            if (Latency > TimeSpan.Zero)
            {
                await Task.Delay(Latency, cancellationToken);
            }

            if (path.EndsWith("/beststories.json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _listCalls);
                if (ListGate is { } listGate)
                {
                    await listGate.Task.WaitAsync(cancellationToken);
                }

                return ListResponder?.Invoke(request) ?? Json(JsonSerializer.Serialize(Ids));
            }

            var match = ItemPath().Match(path);
            if (match.Success)
            {
                Interlocked.Increment(ref _itemCalls);
                var id = long.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                if (ItemGate is { } itemGate)
                {
                    await itemGate.Task.WaitAsync(cancellationToken);
                }

                return ItemResponder?.Invoke(id, request) ?? Json(Items.TryGetValue(id, out var body) ? body : "null");
            }

            return Status(HttpStatusCode.NotFound);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    [GeneratedRegex(@"/item/(\d+)\.json$")]
    private static partial Regex ItemPath();
}

public sealed record RecordedRequest(HttpMethod Method, string Path, string Accept, string UserAgent);
