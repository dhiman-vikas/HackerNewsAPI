using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RestfulAPIDemo.Tests.Fakes;

namespace RestfulAPIDemo.Tests.Integration;

/// <summary>Pins the public HTTP contract of <c>GET /api/stories/best?n=</c>: body shape, ordering, headers and pass-through of Hacker News data.</summary>
public sealed class ContractTests
{
    private const string BestStoriesUrl = "/api/stories/best";

    private static readonly string[] ExpectedPropertyOrder = ["title", "uri", "postedBy", "time", "score", "commentCount"];

    [Fact]
    public async Task Returns_exactly_n_items_sorted_by_score()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50), (3, 30));
        using var client = factory.CreateClient();

        var top2 = await GetArrayAsync(client, BestStoriesUrl + "?n=2");
        var top3 = await GetArrayAsync(client, BestStoriesUrl + "?n=3");

        Assert.Equal(["Story 2", "Story 3"], Titles(top2));
        Assert.Equal([50, 30], Scores(top2));
        var scores3 = Scores(top3);
        Assert.Equal(3, scores3.Length);
        Assert.Equal(scores3.OrderByDescending(s => s).ToArray(), scores3);
    }

    [Fact]
    public async Task Body_is_bare_array_even_for_n_1()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50));
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync(BestStoriesUrl + "?n=1");

        Assert.StartsWith("[", raw, StringComparison.Ordinal);
        Assert.EndsWith("]", raw, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        var only = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(50, only.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Spec_item_body_equals_expected_literal()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(HnFixtures.SpecId, HnFixtures.SpecItemJson);
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync(BestStoriesUrl + "?n=1");

        Assert.Equal("[" + HnFixtures.SpecStoryJson + "]", raw);
    }

    [Fact]
    public async Task Content_type_is_application_json_utf8()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(BestStoriesUrl + "?n=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Elements_have_exactly_six_properties_in_order()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(1, HnFixtures.Story(1, 90));
        factory.Handler.Add(2, HnFixtures.AskHn(2, 80));
        factory.Handler.Add(3, HnFixtures.Poll(3, 70));
        factory.Handler.Add(4, HnFixtures.Job(4));
        factory.Handler.Add(5, HnFixtures.Tricky(5, 60));
        factory.Handler.Add(6, HnFixtures.Story(6, score: null, descendants: null, by: null));
        using var client = factory.CreateClient();

        var items = await GetArrayAsync(client, BestStoriesUrl + "?n=6");

        Assert.Equal(6, items.Length);
        Assert.All(items, element => Assert.Equal(ExpectedPropertyOrder, element.EnumerateObject().Select(p => p.Name).ToArray()));
    }

    [Fact]
    public async Task Uri_null_for_text_post_is_present()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(1, HnFixtures.AskHn(1));
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync(BestStoriesUrl + "?n=1");

        Assert.Contains("\"uri\":null", raw, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        var only = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, only.GetProperty("uri").ValueKind);
    }

    [Fact]
    public async Task PostedBy_null_when_by_missing()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(1, HnFixtures.Story(1, by: null));
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync(BestStoriesUrl + "?n=1");

        Assert.Contains("\"postedBy\":null", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cache_Control_max_age_equals_ttl_right_after_refresh()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(BestStoriesUrl + "?n=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=3600", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal("0", Assert.Single(response.Headers.GetValues("Age")));
    }

    [Fact]
    public async Task Cache_Control_max_age_decreases_with_age()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50));
        using var client = factory.CreateClient();
        Assert.NotNull(await factory.WarmAsync());

        factory.Time.Advance(TimeSpan.FromSeconds(1000));
        using (var response = await client.GetAsync(BestStoriesUrl + "?n=1"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("public, max-age=2600", Assert.Single(response.Headers.GetValues("Cache-Control")));
            Assert.Equal("1000", Assert.Single(response.Headers.GetValues("Age")));
        }

        // Crossing the interval fires the refresher's timer; make that cycle fail so the served snapshot keeps its age.
        factory.Handler.ListResponder = _ => FakeHackerNewsHandler.Status(HttpStatusCode.ServiceUnavailable);
        factory.Time.Advance(TimeSpan.FromSeconds(3000));
        using (var response = await client.GetAsync(BestStoriesUrl + "?n=1"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("public, max-age=0", Assert.Single(response.Headers.GetValues("Cache-Control")));
            Assert.Equal("4000", Assert.Single(response.Headers.GetValues("Age")));
        }
    }

    [Fact]
    public async Task N_greater_than_available_returns_all()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50), (3, 30));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(BestStoriesUrl + "?n=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetArrayLength());
    }

    [Fact]
    public async Task Empty_list_returns_empty_array()
    {
        using var factory = new BestStoriesApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(BestStoriesUrl + "?n=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Duplicate_ids_are_fetched_once_and_returned_once()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(1, HnFixtures.Story(1, 40));
        factory.Handler.Ids.Add(1);
        factory.Handler.Add(2, HnFixtures.Story(2, 30));
        using var client = factory.CreateClient();

        var items = await GetArrayAsync(client, BestStoriesUrl + "?n=10");

        Assert.Equal(2, items.Length);
        Assert.Equal(["Story 1", "Story 2"], Titles(items));
        Assert.Equal(2, factory.Handler.ItemCalls);
    }

    [Fact]
    public async Task Poll_and_job_items_are_included()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(1, HnFixtures.Story(1, 100));
        factory.Handler.Add(2, HnFixtures.Poll(2, 50));
        factory.Handler.Add(3, HnFixtures.Job(3));
        using var client = factory.CreateClient();

        var items = await GetArrayAsync(client, BestStoriesUrl + "?n=3");

        Assert.Equal(3, items.Length);
        Assert.Equal("Poll 2", items[1].GetProperty("title").GetString());
        var job = items[2];
        Assert.Equal("Company (YC W26) is hiring", job.GetProperty("title").GetString());
        Assert.Equal(0, job.GetProperty("score").GetInt32());
        Assert.Equal(0, job.GetProperty("commentCount").GetInt32());
    }

    [Fact]
    public async Task Deleted_dead_null_and_malformed_items_are_excluded_and_not_errors()
    {
        using var factory = new BestStoriesApiFactory();
        for (long id = 1; id <= 8; id++)
        {
            factory.Handler.Add(id, HnFixtures.Story(id, (int)id * 10));
        }

        factory.Handler.Add(9, null); // answers the literal null
        factory.Handler.Add(10, HnFixtures.Deleted(10));
        factory.Handler.Add(11, HnFixtures.Dead(11, 999));
        factory.Handler.Add(12, HnFixtures.Story(12, 999));
        factory.Handler.ItemResponder = (id, _) => id == 12 ? FakeHackerNewsHandler.Html() : null;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(BestStoriesUrl + "?n=12");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var titles = Titles(body.EnumerateArray());
        Assert.Equal(8, titles.Length);
        Assert.Equal(["Story 8", "Story 7", "Story 6", "Story 5", "Story 4", "Story 3", "Story 2", "Story 1"], titles);
        Assert.Empty(factory.Logs.AtLevel(LogLevel.Error));
        Assert.Empty(factory.Logs.AtLevel(LogLevel.Critical));
        var completed = Assert.Single(factory.Logs.Entries, e => e.EventId.Id == 1000);
        Assert.Contains("8 stories published, 3 excluded", completed.Message, StringComparison.Ordinal);
        Assert.Contains("1 unresolved", completed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_descendants_and_score_map_to_zero()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(1, HnFixtures.Story(1, score: null, descendants: null));
        using var client = factory.CreateClient();

        var items = await GetArrayAsync(client, BestStoriesUrl + "?n=1");

        var only = Assert.Single(items);
        Assert.Equal(0, only.GetProperty("score").GetInt32());
        Assert.Equal(0, only.GetProperty("commentCount").GetInt32());
    }

    [Fact]
    public async Task Route_and_parameter_are_case_insensitive()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50), (3, 30));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/API/STORIES/BEST?N=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetArrayLength());
    }

    [Fact]
    public async Task Trailing_slash_is_tolerated()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50), (3, 30));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(BestStoriesUrl + "/?n=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetArrayLength());
    }

    [Fact]
    public async Task Head_returns_200_with_json_content_type()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50), (3, 30));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Head, BestStoriesUrl + "?n=2");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Accept_xml_still_returns_json()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await client.GetAsync(BestStoriesUrl + "?n=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task Two_gets_within_ttl_return_identical_bodies_and_no_extra_upstream_calls()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50), (3, 30));
        using var client = factory.CreateClient();

        var first = await client.GetStringAsync(BestStoriesUrl + "?n=3");
        var callsAfterFirst = factory.Handler.TotalCalls;
        var second = await client.GetStringAsync(BestStoriesUrl + "?n=3");

        Assert.Equal(first, second);
        Assert.Equal(4, callsAfterFirst); // 1 list + 3 items during warm-up
        Assert.Equal(callsAfterFirst, factory.Handler.TotalCalls);
    }

    [Fact]
    public async Task Requests_to_hacker_news_carry_Accept_and_User_Agent()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50), (3, 30));
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync(BestStoriesUrl + "?n=3");

        Assert.NotEmpty(raw);
        var requests = factory.Handler.Requests;
        Assert.Equal(4, requests.Count);
        Assert.All(requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("application/json", r.Accept);
            Assert.Contains("RestfulAPIDemo-BestStories", r.UserAgent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Tricky_title_and_url_are_passed_through_verbatim()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(1, HnFixtures.Tricky(1));
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync(BestStoriesUrl + "?n=1");

        Assert.Contains("&", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0026", raw, StringComparison.Ordinal);
        Assert.Contains(HnFixtures.TrickyUrl, raw, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        var only = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(HnFixtures.TrickyTitle, only.GetProperty("title").GetString());
        Assert.Equal(HnFixtures.TrickyUrl, only.GetProperty("uri").GetString());
    }

    [Fact]
    public async Task Large_n_500_returns_all_500_when_available()
    {
        using var factory = new BestStoriesApiFactory();
        // gcd(37, 500) == 1, so the scores are a permutation of 1..500 in scrambled list order.
        factory.Handler.SetStories(Enumerable.Range(1, 500).Select(i => ((long)i, (i * 37 % 500) + 1)).ToArray());
        using var client = factory.CreateClient();

        var items = await GetArrayAsync(client, BestStoriesUrl + "?n=500");

        var scores = Scores(items);
        Assert.Equal(500, scores.Length);
        Assert.Equal(Enumerable.Range(1, 500).Reverse().ToArray(), scores);
        Assert.Equal(500, factory.Handler.ItemCalls);
    }

    [Fact]
    public async Task Time_is_rendered_with_plus_zero_offset_for_every_item()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(1, HnFixtures.Story(1, 90, time: 1_700_000_000));
        factory.Handler.Add(2, HnFixtures.AskHn(2, 80, time: 0));
        factory.Handler.Add(3, HnFixtures.Poll(3, 70, time: 1_234_567_890));
        factory.Handler.Add(HnFixtures.SpecId, HnFixtures.SpecItemJson);
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync(BestStoriesUrl + "?n=4");

        var times = Regex.Matches(raw, "\"time\":\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(4, times.Length);
        Assert.All(times, t => Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+00:00$", t));
        Assert.Contains("\"time\":\"2023-11-14T22:13:20+00:00\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"time\":\"1970-01-01T00:00:00+00:00\"", raw, StringComparison.Ordinal);
    }

    private static async Task<JsonElement[]> GetArrayAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        return body.EnumerateArray().ToArray();
    }

    private static int[] Scores(IEnumerable<JsonElement> items) => items.Select(e => e.GetProperty("score").GetInt32()).ToArray();

    private static string[] Titles(IEnumerable<JsonElement> items) => items.Select(e => e.GetProperty("title").ToString()).ToArray();
}
