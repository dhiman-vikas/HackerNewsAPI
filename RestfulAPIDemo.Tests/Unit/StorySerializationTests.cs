using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RestfulAPIDemo.Api;
using RestfulAPIDemo.BestStories;
using RestfulAPIDemo.Tests.Fakes;

namespace RestfulAPIDemo.Tests.Unit;

/// <summary>Proves Story serialises with the app's JSON options to the exact contract: pinned property names and order, explicit nulls, ISO-8601 "+00:00" times and verbatim (unescaped) text.</summary>
public sealed class StorySerializationTests
{
    /// <summary>
    /// The same options the API uses: Minimal APIs' <see cref="JsonOptions"/> (Web defaults) configured exactly as in
    /// Program.cs via <c>ConfigureHttpJsonOptions</c>, resolved from a throw-away service provider.
    /// </summary>
    private static readonly JsonSerializerOptions AppOptions = BuildAppOptions();

    /// <summary>Web defaults with the stock encoder, for the contrast test that documents why the app relaxes escaping.</summary>
    private static readonly JsonSerializerOptions DefaultWebOptions = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset SampleTime = new(2019, 10, 12, 13, 43, 1, TimeSpan.Zero);

    private static JsonSerializerOptions BuildAppOptions()
    {
        var services = new ServiceCollection();
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
    }

    private static Story MapSpecItem()
    {
        Assert.True(StoryMapper.TryMap(HnFixtures.SpecItem, out var story, out _));
        Assert.NotNull(story);
        return story;
    }

    [Fact]
    public void Spec_item_serializes_to_exact_sample_string()
    {
        var story = MapSpecItem();

        var json = JsonSerializer.Serialize(story, AppOptions);

        Assert.Equal(HnFixtures.SpecStoryJson, json);
    }

    [Fact]
    public void Property_order_is_title_uri_postedBy_time_score_commentCount()
    {
        var json = JsonSerializer.Serialize(MapSpecItem(), AppOptions);

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["title", "uri", "postedBy", "time", "score", "commentCount"], names);
    }

    [Fact]
    public void Null_uri_is_emitted_not_omitted()
    {
        var story = new Story(Title: "Ask HN: Question", Uri: null, PostedBy: "asker", Time: SampleTime, Score: 1, CommentCount: 2);

        var json = JsonSerializer.Serialize(story, AppOptions);

        Assert.Contains("\"uri\":null", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("uri").ValueKind);
    }

    [Fact]
    public void Null_postedBy_is_emitted_not_omitted()
    {
        var story = new Story(Title: "Orphaned", Uri: "https://example.com/1", PostedBy: null, Time: SampleTime, Score: 1, CommentCount: 2);

        var json = JsonSerializer.Serialize(story, AppOptions);

        Assert.Contains("\"postedBy\":null", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("postedBy").ValueKind);
    }

    [Fact]
    public void Null_title_is_emitted_not_omitted()
    {
        var story = new Story(Title: null, Uri: "https://example.com/1", PostedBy: "user", Time: SampleTime, Score: 1, CommentCount: 2);

        var json = JsonSerializer.Serialize(story, AppOptions);

        Assert.Contains("\"title\":null", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("title").ValueKind);
    }

    [Fact]
    public void All_nullable_fields_null_still_emits_six_properties()
    {
        var story = new Story(Title: null, Uri: null, PostedBy: null, Time: SampleTime, Score: 0, CommentCount: 0);

        var json = JsonSerializer.Serialize(story, AppOptions);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(6, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Score_and_commentCount_are_numbers()
    {
        var json = JsonSerializer.Serialize(MapSpecItem(), AppOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Number, root.GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("commentCount").ValueKind);
        Assert.Equal(1716, root.GetProperty("score").GetInt32());
        Assert.Equal(572, root.GetProperty("commentCount").GetInt32());
    }

    [Fact]
    public void Time_matches_iso_pattern_with_plus_zero_offset()
    {
        var json = JsonSerializer.Serialize(MapSpecItem(), AppOptions);

        using var document = JsonDocument.Parse(json);
        var time = document.RootElement.GetProperty("time");

        Assert.Equal(JsonValueKind.String, time.ValueKind);
        var text = time.GetString();
        Assert.NotNull(text);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+00:00$", text);
        Assert.EndsWith("+00:00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Z", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Relaxed_escaping_emits_ampersand_and_non_ascii_verbatim()
    {
        var story = new Story(Title: HnFixtures.TrickyTitle, Uri: HnFixtures.TrickyUrl, PostedBy: "caf\u00e9", Time: SampleTime, Score: 1, CommentCount: 0);

        var json = JsonSerializer.Serialize(story, AppOptions);

        // '&', '<', '>', e-acute, the em dash and the curly quotes are all emitted as-is...
        Assert.Contains("\"title\":\"Caf\u00e9 & Bar <b>bold</b> \u2014 \u201Cquotes\u201D ", json, StringComparison.Ordinal);
        Assert.Contains("\"uri\":\"" + HnFixtures.TrickyUrl + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"postedBy\":\"caf\u00e9\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0026", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00E9", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u2014", json, StringComparison.Ordinal);
        // ...while the rocket emoji (a supplementary-plane character) is the one thing every built-in
        // JavaScriptEncoder still escapes, because UnicodeRanges only spans the BMP. Still valid JSON.
        Assert.Contains("\\uD83D\\uDE80", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(HnFixtures.TrickyTitle, document.RootElement.GetProperty("title").GetString());
        Assert.Equal(HnFixtures.TrickyUrl, document.RootElement.GetProperty("uri").GetString());
        Assert.Equal("caf\u00e9", document.RootElement.GetProperty("postedBy").GetString());
    }

    [Fact]
    public void Default_encoder_would_have_escaped_it()
    {
        var story = new Story(Title: HnFixtures.TrickyTitle, Uri: HnFixtures.TrickyUrl, PostedBy: null, Time: SampleTime, Score: 1, CommentCount: 0);

        var json = JsonSerializer.Serialize(story, DefaultWebOptions);

        Assert.Contains("\\u0026", json, StringComparison.Ordinal);
        Assert.DoesNotContain("&", json, StringComparison.Ordinal);
        Assert.DoesNotContain(HnFixtures.TrickyTitle, json, StringComparison.Ordinal);
        // Escaped output is still the same value once parsed.
        using var document = JsonDocument.Parse(json);
        Assert.Equal(HnFixtures.TrickyTitle, document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Epoch_zero_time_serializes_as_1970()
    {
        var story = new Story(Title: "t", Uri: null, PostedBy: null, Time: DateTimeOffset.FromUnixTimeSeconds(0), Score: 0, CommentCount: 0);

        var json = JsonSerializer.Serialize(story, AppOptions);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("1970-01-01T00:00:00+00:00", document.RootElement.GetProperty("time").GetString());
    }

    [Fact]
    public void Deserializing_spec_story_json_and_reserializing_is_identity()
    {
        var story = JsonSerializer.Deserialize<Story>(HnFixtures.SpecStoryJson, AppOptions);

        Assert.NotNull(story);
        Assert.Equal(MapSpecItem(), story);
        Assert.Equal(HnFixtures.SpecStoryJson, JsonSerializer.Serialize(story, AppOptions));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Serialization_is_culture_invariant(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var json = JsonSerializer.Serialize(MapSpecItem(), AppOptions);

            Assert.Equal(HnFixtures.SpecStoryJson, json);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
