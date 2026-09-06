using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RestfulAPIDemo.Tests.Fakes;

namespace RestfulAPIDemo.Tests.Integration;

/// <summary>Minimal end-to-end checks that prove the test host, fake handler and fake clock are wired correctly.</summary>
public sealed class SmokeTests
{
    [Fact]
    public async Task Returns_best_n_stories_sorted_by_score()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10), (2, 50), (3, 30));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/stories/best?n=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal([50, 30], body.EnumerateArray().Select(e => e.GetProperty("score").GetInt32()).ToArray());
        Assert.Equal(1, factory.Handler.ListCalls);
        Assert.Equal(3, factory.Handler.ItemCalls);
    }

    [Fact]
    public async Task Spec_item_round_trips_to_the_exact_sample_body()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.Add(HnFixtures.SpecId, HnFixtures.SpecItemJson);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/api/stories/best?n=1");

        Assert.Equal("[" + HnFixtures.SpecStoryJson + "]", body);
    }

    [Fact]
    public async Task Invalid_n_is_a_validation_problem()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.SetStories((1, 10));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/stories/best?n=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Single(problem.GetProperty("errors").GetProperty("n").EnumerateArray());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Cold_start_with_failing_upstream_is_503_with_retry_after()
    {
        using var factory = new BestStoriesApiFactory();
        factory.Handler.ListResponder = _ => FakeHackerNewsHandler.Status(HttpStatusCode.InternalServerError);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/stories/best?n=1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(2, factory.Handler.ListCalls); // 1 attempt + 1 retry, no delay
    }
}
