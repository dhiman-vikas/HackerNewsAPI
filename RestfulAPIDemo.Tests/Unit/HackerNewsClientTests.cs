using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulAPIDemo.HackerNews;
using RestfulAPIDemo.Tests.Fakes;

namespace RestfulAPIDemo.Tests.Unit;

/// <summary>
/// Proves that <see cref="HackerNewsClient"/> maps every HTTP outcome of the two Hacker News endpoints to the
/// documented result (ids array / null, Found / Missing / Failed) without throwing, except for the caller's own
/// cancellation of a list request, and that the real registration sends the expected headers.
/// </summary>
public sealed class HackerNewsClientTests
{
    private const string BaseAddress = "https://hacker-news.firebaseio.com/v0/";

    [Fact]
    public async Task List_array_is_returned()
    {
        using var h = new Harness();
        h.Handler.Ids.AddRange([1, 2, 3]);

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.NotNull(ids);
        Assert.Equal([1L, 2L, 3L], ids);
        Assert.Equal(1, h.Handler.ListCalls);
    }

    [Fact]
    public async Task List_empty_array_is_returned_as_empty()
    {
        using var h = new Harness();

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.NotNull(ids);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task List_null_body_returns_null()
    {
        using var h = new Harness();
        h.Handler.ListResponder = _ => FakeHackerNewsHandler.Json("null");

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Null(ids);
        var warning = Assert.Single(h.Logs.AtLevel(LogLevel.Warning));
        Assert.Contains("body was null", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_object_body_returns_null()
    {
        using var h = new Harness();
        h.Handler.ListResponder = _ => FakeHackerNewsHandler.Json("""{"a":1}""");

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Null(ids);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public async Task List_html_body_returns_null(string contentType)
    {
        using var h = new Harness();
        h.Handler.ListResponder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(HnFixtures.HtmlErrorPage, Encoding.UTF8, contentType),
        };

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Null(ids);
        var warning = Assert.Single(h.Logs.AtLevel(LogLevel.Warning));
        Assert.Contains("JsonException", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_non_integer_ids_returns_null()
    {
        using var h = new Harness();
        h.Handler.ListResponder = _ => FakeHackerNewsHandler.Json("""["a","b"]""");

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Null(ids);
    }

    [Fact]
    public async Task List_500_returns_null()
    {
        using var h = new Harness();
        h.Handler.ListResponder = _ => FakeHackerNewsHandler.Status(HttpStatusCode.InternalServerError);

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Null(ids);
        Assert.Equal(1, h.Handler.ListCalls); // no resilience pipeline in this harness: exactly one attempt
        var warning = Assert.Single(h.Logs.AtLevel(LogLevel.Warning));
        Assert.Equal(2000, warning.EventId.Id);
        Assert.Contains("beststories.json", warning.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 500", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_404_returns_null()
    {
        using var h = new Harness();
        h.Handler.ListResponder = _ => FakeHackerNewsHandler.Status(HttpStatusCode.NotFound);

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Null(ids);
        var warning = Assert.Single(h.Logs.AtLevel(LogLevel.Warning));
        Assert.Contains("HTTP 404", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_truncated_json_returns_null()
    {
        using var h = new Harness();
        h.Handler.ListResponder = _ => FakeHackerNewsHandler.Json("[1,2,");

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Null(ids);
    }

    [Fact]
    public async Task List_transport_exception_returns_null()
    {
        using var h = new Harness();
        h.Handler.ListResponder = _ => throw new HttpRequestException("connection refused");

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Null(ids);
        var warning = Assert.Single(h.Logs.AtLevel(LogLevel.Warning));
        Assert.Contains("HttpRequestException", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_caller_cancellation_is_rethrown()
    {
        using var h = new Harness();
        h.Handler.ListGate = new TaskCompletionSource(); // never opened: the request can only end by cancellation
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Client.GetBestStoryIdsAsync(cancelled));

        Assert.Empty(h.Logs.Entries);
    }

    [Fact]
    public async Task Item_json_is_Found_and_tolerates_unknown_fields()
    {
        using var h = new Harness();
        h.Handler.Items[42] =
            """{"by":"alice","descendants":5,"foo":1,"id":42,"kids":[421,422],"parts":[4201,4202],"score":77,"text":"Vote","time":1700000000,"title":"Poll 42","type":"poll","url":"https://example.com/42"}""";

        var result = await h.Client.GetItemAsync(42, CancellationToken.None);

        Assert.Equal(ItemStatus.Found, result.Status);
        Assert.Null(result.Reason);
        Assert.Equal(
            new HackerNewsItem(
                Id: 42, Type: "poll", By: "alice", Time: 1_700_000_000, Title: "Poll 42",
                Url: "https://example.com/42", Score: 77, Descendants: 5, Deleted: null, Dead: null),
            result.Item);
    }

    [Fact]
    public async Task Item_spec_story_is_parsed_field_by_field()
    {
        using var h = new Harness();
        h.Handler.Items[HnFixtures.SpecId] = HnFixtures.SpecItemJson;

        var result = await h.Client.GetItemAsync(HnFixtures.SpecId, CancellationToken.None);

        Assert.Equal(ItemStatus.Found, result.Status);
        Assert.Equal(HnFixtures.SpecItem, result.Item);
    }

    [Fact]
    public async Task Item_null_body_is_Missing()
    {
        using var h = new Harness(); // no body registered for id 42: the fake answers the literal null

        var result = await h.Client.GetItemAsync(42, CancellationToken.None);

        Assert.Equal(ItemStatus.Missing, result.Status);
        Assert.Equal("null body", result.Reason);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task Item_404_is_Missing()
    {
        using var h = new Harness();
        h.Handler.ItemResponder = (_, _) => FakeHackerNewsHandler.Status(HttpStatusCode.NotFound);

        var result = await h.Client.GetItemAsync(42, CancellationToken.None);

        Assert.Equal(ItemStatus.Missing, result.Status);
        Assert.Equal("HTTP 404", result.Reason);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task Item_500_is_Failed()
    {
        using var h = new Harness();
        h.Handler.ItemResponder = (_, _) => FakeHackerNewsHandler.Status(HttpStatusCode.InternalServerError);

        var result = await h.Client.GetItemAsync(42, CancellationToken.None);

        Assert.Equal(ItemStatus.Failed, result.Status);
        Assert.Equal("HTTP 500", result.Reason);
        Assert.Null(result.Item);
        Assert.Equal(1, h.Handler.ItemCalls);
        var debug = Assert.Single(h.Logs.AtLevel(LogLevel.Debug));
        Assert.Equal(2001, debug.EventId.Id);
        Assert.Contains("item/42.json", debug.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 500", debug.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Item_html_body_is_Failed()
    {
        using var h = new Harness();
        h.Handler.ItemResponder = (_, _) => FakeHackerNewsHandler.Html();

        var result = await h.Client.GetItemAsync(42, CancellationToken.None);

        Assert.Equal(ItemStatus.Failed, result.Status);
        Assert.Equal("JsonException", result.Reason);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task Item_transport_exception_is_Failed()
    {
        using var h = new Harness();
        h.Handler.ItemResponder = (_, _) => throw new HttpRequestException("connection reset");

        var result = await h.Client.GetItemAsync(42, CancellationToken.None);

        Assert.Equal(ItemStatus.Failed, result.Status);
        Assert.Equal("HttpRequestException", result.Reason);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task Item_cancelled_token_is_Failed_cancelled()
    {
        using var h = new Harness();
        h.Handler.ItemGate = new TaskCompletionSource(); // never opened: the request can only end by cancellation
        var cancelled = new CancellationToken(canceled: true);

        var result = await h.Client.GetItemAsync(42, cancelled);

        Assert.Equal(ItemStatus.Failed, result.Status);
        Assert.Equal("cancelled", result.Reason);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task Item_deleted_and_dead_flags_are_parsed()
    {
        using var h = new Harness();
        h.Handler.Items[7] = HnFixtures.Deleted(7);
        h.Handler.Items[8] = HnFixtures.Dead(8);

        var deleted = await h.Client.GetItemAsync(7, CancellationToken.None);
        var dead = await h.Client.GetItemAsync(8, CancellationToken.None);

        Assert.Equal(ItemStatus.Found, deleted.Status);
        Assert.NotNull(deleted.Item);
        Assert.True(deleted.Item.Deleted);
        Assert.Null(deleted.Item.Dead);

        Assert.Equal(ItemStatus.Found, dead.Status);
        Assert.NotNull(dead.Item);
        Assert.True(dead.Item.Dead);
        Assert.Null(dead.Item.Deleted);
    }

    [Fact]
    public async Task Ids_deserialize_as_long()
    {
        using var h = new Harness();
        h.Handler.Ids.Add(9_223_372_036_854_775_000L);

        var ids = await h.Client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.NotNull(ids);
        Assert.Equal([9_223_372_036_854_775_000L], ids);
    }

    [Fact]
    public async Task Requests_use_relative_paths_under_base_address()
    {
        using var h = new Harness();
        h.Handler.Ids.Add(42);
        h.Handler.Items[42] = HnFixtures.Story(42);

        await h.Client.GetBestStoryIdsAsync(CancellationToken.None);
        await h.Client.GetItemAsync(42, CancellationToken.None);

        Assert.Equal(["/v0/beststories.json", "/v0/item/42.json"], h.Handler.Requests.Select(r => r.Path).ToArray());
        Assert.All(h.Handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Client_asks_the_factory_for_the_named_HackerNews_client()
    {
        using var h = new Harness();

        await h.Client.GetBestStoryIdsAsync(CancellationToken.None);
        await h.Client.GetItemAsync(1, CancellationToken.None);

        Assert.Equal([HackerNewsClient.HttpClientName, HackerNewsClient.HttpClientName], h.RequestedClientNames.ToArray());
    }

    [Fact]
    public async Task Through_the_real_registration_requests_carry_Accept_and_User_Agent()
    {
        using var handler = new FakeHackerNewsHandler();
        handler.Items[1] = HnFixtures.Story(1);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HackerNews:RetryBaseDelay"] = "00:00:00" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddHackerNewsClient();
        services.AddHttpClient(HackerNewsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHackerNewsClient>();

        var result = await client.GetItemAsync(1, CancellationToken.None);

        Assert.Equal(ItemStatus.Found, result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v0/item/1.json", request.Path);
        Assert.Equal("application/json", request.Accept);
        Assert.Contains("RestfulAPIDemo-BestStories", request.UserAgent, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="HackerNewsClient"/> over a bare <see cref="HttpClient"/> (no resilience pipeline) that talks to a
    /// <see cref="FakeHackerNewsHandler"/>, with every log entry captured.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILoggerFactory _loggerFactory;
        private readonly RecordingHttpClientFactory _factory;

        public Harness()
        {
            Handler = new FakeHackerNewsHandler();
            _httpClient = new HttpClient(Handler, disposeHandler: false) { BaseAddress = new Uri(BaseAddress, UriKind.Absolute) };
            _factory = new RecordingHttpClientFactory(_httpClient);
            Logs = new CapturingLoggerProvider();
            _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            Client = new HackerNewsClient(_factory, _loggerFactory.CreateLogger<HackerNewsClient>());
        }

        public FakeHackerNewsHandler Handler { get; }

        public CapturingLoggerProvider Logs { get; }

        public HackerNewsClient Client { get; }

        public IReadOnlyList<string> RequestedClientNames => _factory.RequestedNames;

        public void Dispose()
        {
            _httpClient.Dispose();
            _loggerFactory.Dispose();
            Handler.Dispose();
        }
    }

    /// <summary>Hands out one pre-built <see cref="HttpClient"/> and remembers which names were asked for.</summary>
    private sealed class RecordingHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return client;
        }
    }
}
