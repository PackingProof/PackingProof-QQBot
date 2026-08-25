using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class TrackingNumberParserTests
{
    [Theory]
    [InlineData("YT123456", "YT123456")]
    [InlineData("查：sf-123456", "SF-123456")]
    [InlineData("  查  12345678  ", "12345678")]
    [InlineData("<@!123456789> SF1234567890", "SF1234567890")]
    [InlineData("<@bot-openid> 6974412900385", "6974412900385")]
    public void TryParse_AcceptsStandaloneTrackingNumber(string content, string expected)
    {
        Assert.True(TrackingNumberParser.TryParse(content, out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("查一下YT123456")]
    [InlineData("hello world")]
    [InlineData("1234")]
    [InlineData("<@!123456789>")]
    public void TryParse_RejectsNonCommandContent(string content) =>
        Assert.False(TrackingNumberParser.TryParse(content, out _));

    [Fact]
    public void NormalizeCommandContent_AllowsContinueAfterGroupMention() =>
        Assert.Equal("继续", TrackingNumberParser.NormalizeCommandContent(" <@!123456789> 继续 "));

    [Fact]
    public void TryParseMany_AcceptsCommonSeparatorsAndRemovesDuplicates()
    {
        Assert.True(TrackingNumberParser.TryParseMany(
            "<@!bot-id> 查：sf123456, YT-123456\n6974412900385，SF123456、JD123456；ZTO123456|EMS123456/DB123456\\YZ123456",
            out string[] numbers,
            out string error));

        Assert.Equal("", error);
        Assert.Equal(
            ["SF123456", "YT-123456", "6974412900385", "JD123456", "ZTO123456", "EMS123456", "DB123456", "YZ123456"],
            numbers);
    }

    [Fact]
    public void TryParseMany_RejectsMoreThanTenNumbers()
    {
        string content = string.Join(' ', Enumerable.Range(100000, 11));

        Assert.False(TrackingNumberParser.TryParseMany(content, out string[] numbers, out string error));
        Assert.Empty(numbers);
        Assert.Contains("最多查询 10 个", error, StringComparison.Ordinal);
    }
}
