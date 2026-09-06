using System.Globalization;
using RestfulAPIDemo.BestStories;
using RestfulAPIDemo.HackerNews;
using RestfulAPIDemo.Tests.Fakes;

namespace RestfulAPIDemo.Tests.Unit;

/// <summary>Proves StoryMapper.TryMap maps every field verbatim, defaults absent optionals, and excludes deleted/dead/time-less items with the documented reasons.</summary>
public sealed class StoryMapperTests
{
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();

    [Fact]
    public void Maps_spec_item_field_for_field()
    {
        var mapped = StoryMapper.TryMap(HnFixtures.SpecItem, out var story, out var reason);

        Assert.True(mapped);
        Assert.Null(reason);
        Assert.NotNull(story);
        Assert.Equal("A uBlock Origin update was rejected from the Chrome Web Store", story.Title);
        Assert.Equal("https://github.com/uBlockOrigin/uBlock-issues/issues/745", story.Uri);
        Assert.Equal("ismaildonmez", story.PostedBy);
        Assert.Equal(new DateTimeOffset(2019, 10, 12, 13, 43, 1, TimeSpan.Zero), story.Time);
        Assert.Equal(TimeSpan.Zero, story.Time.Offset);
        Assert.Equal(1716, story.Score);
        Assert.Equal(572, story.CommentCount);
    }

    [Fact]
    public void Missing_url_maps_to_null_uri()
    {
        var item = HnFixtures.SpecItem with { Url = null };

        Assert.True(StoryMapper.TryMap(item, out var story, out _));
        Assert.NotNull(story);
        Assert.Null(story.Uri);
        Assert.Equal(HnFixtures.SpecItem.Title, story.Title);
    }

    [Fact]
    public void Missing_by_maps_to_null_postedBy()
    {
        var item = HnFixtures.SpecItem with { By = null };

        Assert.True(StoryMapper.TryMap(item, out var story, out _));
        Assert.NotNull(story);
        Assert.Null(story.PostedBy);
        Assert.Equal(HnFixtures.SpecItem.Url, story.Uri);
    }

    [Fact]
    public void Missing_title_maps_to_null_title()
    {
        var item = HnFixtures.SpecItem with { Title = null };

        Assert.True(StoryMapper.TryMap(item, out var story, out _));
        Assert.NotNull(story);
        Assert.Null(story.Title);
        Assert.Equal(HnFixtures.SpecItem.Url, story.Uri);
    }

    [Fact]
    public void Missing_descendants_maps_to_zero()
    {
        var item = HnFixtures.SpecItem with { Descendants = null };

        Assert.True(StoryMapper.TryMap(item, out var story, out _));
        Assert.NotNull(story);
        Assert.Equal(0, story.CommentCount);
        Assert.Equal(1716, story.Score);
    }

    [Fact]
    public void Missing_score_maps_to_zero()
    {
        var item = HnFixtures.SpecItem with { Score = null };

        Assert.True(StoryMapper.TryMap(item, out var story, out _));
        Assert.NotNull(story);
        Assert.Equal(0, story.Score);
        Assert.Equal(572, story.CommentCount);
    }

    [Fact]
    public void Deleted_is_not_mapped()
    {
        var item = new HackerNewsItem(Id: 1, Type: "story", By: null, Time: 1_700_000_000, Title: null, Url: null, Score: null, Descendants: null, Deleted: true, Dead: null);

        var mapped = StoryMapper.TryMap(item, out var story, out var reason);

        Assert.False(mapped);
        Assert.Null(story);
        Assert.Equal("deleted", reason);
    }

    [Fact]
    public void Dead_is_not_mapped()
    {
        var item = FakeHackerNewsClient.Item(2, score: 100, dead: true);

        var mapped = StoryMapper.TryMap(item, out var story, out var reason);

        Assert.False(mapped);
        Assert.Null(story);
        Assert.Equal("dead", reason);
    }

    [Fact]
    public void Missing_time_is_not_mapped()
    {
        var item = FakeHackerNewsClient.Item(3, time: null);

        var mapped = StoryMapper.TryMap(item, out var story, out var reason);

        Assert.False(mapped);
        Assert.Null(story);
        Assert.Equal("no time", reason);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(253_402_300_800L)] // DateTimeOffset.MaxValue.ToUnixTimeSeconds() + 1
    [InlineData(-62_135_596_801L)] // DateTimeOffset.MinValue.ToUnixTimeSeconds() - 1
    public void Time_out_of_range_is_not_mapped(long time)
    {
        var item = FakeHackerNewsClient.Item(4, time: time);

        var mapped = StoryMapper.TryMap(item, out var story, out var reason);

        Assert.False(mapped);
        Assert.Null(story);
        Assert.Equal("time out of range", reason);
    }

    [Fact]
    public void Boundary_times_are_mapped()
    {
        Assert.True(StoryMapper.TryMap(FakeHackerNewsClient.Item(5, time: MaxUnixSeconds), out var max, out _));
        Assert.True(StoryMapper.TryMap(FakeHackerNewsClient.Item(6, time: MinUnixSeconds), out var min, out _));

        Assert.NotNull(max);
        Assert.NotNull(min);
        Assert.Equal(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero), max.Time);
        Assert.Equal(DateTimeOffset.MinValue, min.Time);
    }

    [Fact]
    public void Deleted_takes_precedence_over_other_exclusions()
    {
        var item = new HackerNewsItem(Id: 7, Type: null, By: null, Time: null, Title: null, Url: null, Score: null, Descendants: null, Deleted: true, Dead: true);

        Assert.False(StoryMapper.TryMap(item, out _, out var reason));
        Assert.Equal("deleted", reason);
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("job")]
    [InlineData("comment")]
    [InlineData(null)]
    public void Poll_and_job_types_are_mapped(string? type)
    {
        var item = new HackerNewsItem(Id: 8, Type: type, By: "hiring", Time: 1_700_000_000, Title: "Company (YC W26) is hiring", Url: "https://example.com/jobs/8", Score: null, Descendants: null, Deleted: null, Dead: null);

        var mapped = StoryMapper.TryMap(item, out var story, out var reason);

        Assert.True(mapped);
        Assert.Null(reason);
        Assert.NotNull(story);
        Assert.Equal("Company (YC W26) is hiring", story.Title);
        Assert.Equal("https://example.com/jobs/8", story.Uri);
        Assert.Equal("hiring", story.PostedBy);
        Assert.Equal(0, story.Score);
        Assert.Equal(0, story.CommentCount);
    }

    [Fact]
    public void Values_pass_through_unchanged()
    {
        const string title = "Foo &amp; Bar ";
        var item = FakeHackerNewsClient.Item(9, title: title, url: HnFixtures.TrickyUrl, by: "  spaced  ");

        Assert.True(StoryMapper.TryMap(item, out var story, out _));
        Assert.NotNull(story);
        Assert.Equal(title, story.Title);
        Assert.Equal(HnFixtures.TrickyUrl, story.Uri);
        Assert.Equal("  spaced  ", story.PostedBy);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Time_has_zero_offset_regardless_of_culture(string cultureName)
    {
        var expected = new DateTimeOffset(2019, 10, 12, 13, 43, 1, TimeSpan.Zero);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            Assert.True(StoryMapper.TryMap(HnFixtures.SpecItem, out var story, out _));

            Assert.NotNull(story);
            Assert.Equal(TimeSpan.Zero, story.Time.Offset);
            Assert.Equal(expected, story.Time);
            Assert.Equal(expected.Ticks, story.Time.Ticks);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Epoch_zero_time_maps_to_1970()
    {
        var item = FakeHackerNewsClient.Item(10, time: 0);

        Assert.True(StoryMapper.TryMap(item, out var story, out _));
        Assert.NotNull(story);
        Assert.Equal(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), story.Time);
        Assert.Equal(TimeSpan.Zero, story.Time.Offset);
    }

    [Fact]
    public void Null_item_throws()
    {
        Assert.Throws<ArgumentNullException>(() => StoryMapper.TryMap(null!, out _, out _));
    }
}
