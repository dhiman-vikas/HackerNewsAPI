using Microsoft.Extensions.Primitives;
using RestfulAPIDemo.Api;

namespace RestfulAPIDemo.Tests.Unit;

/// <summary>Proves NParser.TryParse accepts exactly one plain decimal integer in [1, max] and reports the exact validation message for every failure mode.</summary>
public sealed class NParserTests
{
    private const int Max = 500;
    private const string Required = "The query parameter 'n' is required.";
    private const string ExactlyOnce = "The query parameter 'n' must be specified exactly once.";
    private const string Between1And500 = "The query parameter 'n' must be an integer between 1 and 500.";

    [Theory]
    [InlineData("1", 1)]
    [InlineData("500", 500)]
    [InlineData("010", 10)]
    [InlineData("0500", 500)]
    [InlineData("42", 42)]
    public void Valid_values(string raw, int expected)
    {
        var ok = NParser.TryParse(new StringValues(raw), Max, out var n, out var error);

        Assert.True(ok);
        Assert.Equal(expected, n);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(null, Required)] // absent: StringValues.Empty
    [InlineData("", Required)]
    [InlineData("abc", Between1And500)]
    [InlineData("1.5", Between1And500)]
    [InlineData("1e2", Between1And500)]
    [InlineData(" 10", Between1And500)]
    [InlineData("10 ", Between1And500)]
    [InlineData("\t10", Between1And500)]
    [InlineData("+10", Between1And500)]
    [InlineData("-1", Between1And500)]
    [InlineData("0", Between1And500)]
    [InlineData("00", Between1And500)]
    [InlineData("501", Between1And500)]
    [InlineData("1,000", Between1And500)]
    [InlineData("0x1F", Between1And500)]
    [InlineData("1_0", Between1And500)]
    [InlineData("2147483648", Between1And500)]
    [InlineData("99999999999999999999", Between1And500)]
    [InlineData("\u0661\u0662", Between1And500)] // Arabic-Indic digits are not ASCII digits
    public void Invalid_values(string? raw, string expectedError)
    {
        var values = raw is null ? StringValues.Empty : new StringValues(raw);

        var ok = NParser.TryParse(values, Max, out _, out var error);

        Assert.False(ok);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData(new object[] { new[] { "1", "2" } })]
    [InlineData(new object[] { new[] { "7", "7" } })]
    [InlineData(new object[] { new[] { "", "" } })]
    [InlineData(new object[] { new[] { "1", "2", "3" } })]
    public void Repeated_values_are_rejected(string[] values)
    {
        var ok = NParser.TryParse(new StringValues(values), Max, out _, out var error);

        Assert.False(ok);
        Assert.Equal(ExactlyOnce, error);
    }

    [Fact]
    public void Message_includes_configured_max()
    {
        var rejected = NParser.TryParse(new StringValues("4"), 3, out _, out var error);

        Assert.False(rejected);
        Assert.Equal("The query parameter 'n' must be an integer between 1 and 3.", error);
        Assert.Contains("between 1 and 3", error, StringComparison.Ordinal);

        var accepted = NParser.TryParse(new StringValues("3"), 3, out var n, out var noError);

        Assert.True(accepted);
        Assert.Equal(3, n);
        Assert.Empty(noError);
    }

    [Fact]
    public void Single_empty_value_counts_as_missing()
    {
        var ok = NParser.TryParse(new StringValues(""), Max, out var n, out var error);

        Assert.False(ok);
        Assert.Equal(0, n);
        Assert.Equal(Required, error);
    }

    [Fact]
    public void Default_StringValues_counts_as_missing()
    {
        var ok = NParser.TryParse(default, Max, out var n, out var error);

        Assert.False(ok);
        Assert.Equal(0, n);
        Assert.Equal(Required, error);
    }
}
