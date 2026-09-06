using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RestfulAPIDemo.HackerNews;

namespace RestfulAPIDemo.Tests.Fakes;

/// <summary>Canonical Hacker News JSON bodies used across the test suite.</summary>
public static class HnFixtures
{
    /// <summary>The story used as the example in the problem statement.</summary>
    public const long SpecId = 21233041;

    /// <summary>Exactly what Hacker News returns for the spec story (kids trimmed).</summary>
    public const string SpecItemJson =
        """{"by":"ismaildonmez","descendants":572,"id":21233041,"kids":[21233229,21233577,21235077],"score":1716,"time":1570887781,"title":"A uBlock Origin update was rejected from the Chrome Web Store","type":"story","url":"https://github.com/uBlockOrigin/uBlock-issues/issues/745"}""";

    /// <summary>The expected API representation of the spec story, minified, from the problem statement.</summary>
    public const string SpecStoryJson =
        """{"title":"A uBlock Origin update was rejected from the Chrome Web Store","uri":"https://github.com/uBlockOrigin/uBlock-issues/issues/745","postedBy":"ismaildonmez","time":"2019-10-12T13:43:01+00:00","score":1716,"commentCount":572}""";

    public const string HtmlErrorPage = "<html><head><title>503</title></head><body>Service Unavailable</body></html>";

    /// <summary>A title containing '&amp;', angle brackets, an em dash, curly quotes and an emoji.</summary>
    public const string TrickyTitle = "Café & Bar <b>bold</b> — “quotes” 🚀";

    /// <summary>A URL with percent-encoding, '&amp;', '+', a fragment and an upper-case host.</summary>
    public const string TrickyUrl = "https://Example.com/A%20b?x=1&y=2+3#frag";

    public static readonly HackerNewsItem SpecItem = new(
        Id: SpecId, Type: "story", By: "ismaildonmez", Time: 1570887781,
        Title: "A uBlock Origin update was rejected from the Chrome Web Store",
        Url: "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
        Score: 1716, Descendants: 572, Deleted: null, Dead: null);

    private static readonly JsonSerializerOptions WriteOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>A regular story. Pass <see langword="null"/> explicitly to omit an optional field.</summary>
    public static string Story(
        long id,
        int? score = 100,
        string? title = "Story {id}",
        string? url = "https://example.com/{id}",
        string? by = "user{id}",
        long? time = 1_700_000_000,
        int? descendants = 10,
        string? type = "story",
        bool includeKids = true)
    {
        var idText = id.ToString(CultureInfo.InvariantCulture);
        var doc = new Dictionary<string, object?>
        {
            ["by"] = by?.Replace("{id}", idText, StringComparison.Ordinal),
            ["descendants"] = descendants,
            ["id"] = id,
            ["kids"] = includeKids ? new[] { id * 10 + 1, id * 10 + 2 } : null,
            ["score"] = score,
            ["time"] = time,
            ["title"] = title?.Replace("{id}", idText, StringComparison.Ordinal),
            ["type"] = type,
            ["url"] = url?.Replace("{id}", idText, StringComparison.Ordinal),
        };
        return JsonSerializer.Serialize(doc, WriteOptions);
    }

    /// <summary>A story whose title and URL contain characters that JSON encoders like to escape.</summary>
    public static string Tricky(long id, int score = 100) => Story(id, score, title: TrickyTitle, url: TrickyUrl, by: "café");

    /// <summary>An "Ask HN" style text post: has <c>text</c>, no <c>url</c>.</summary>
    public static string AskHn(long id, int score = 100, long time = 1_700_000_000) =>
        $$"""{"by":"asker{{id}}","descendants":42,"id":{{id}},"score":{{score}},"text":"What do you think?","time":{{time}},"title":"Ask HN: Question {{id}}","type":"story"}""";

    /// <summary>A poll that appears in the best stories list.</summary>
    public static string Poll(long id, int score = 100, long time = 1_700_000_000) =>
        $$"""{"by":"pollster","descendants":7,"id":{{id}},"parts":[{{id * 10 + 1}},{{id * 10 + 2}}],"score":{{score}},"text":"Vote","time":{{time}},"title":"Poll {{id}}","type":"poll"}""";

    /// <summary>A job posting (no score, no descendants).</summary>
    public static string Job(long id, long time = 1_700_000_000) =>
        $$"""{"by":"hiring","id":{{id}},"time":{{time}},"title":"Company (YC W26) is hiring","type":"job","url":"https://example.com/jobs/{{id}}"}""";

    public static string Deleted(long id) =>
        $$"""{"deleted":true,"id":{{id}},"time":1700000000,"type":"story"}""";

    public static string Dead(long id, int score = 100) =>
        $$"""{"by":"spammer","dead":true,"descendants":0,"id":{{id}},"score":{{score}},"time":1700000000,"title":"Dead story","type":"story","url":"https://example.com/dead"}""";
}
