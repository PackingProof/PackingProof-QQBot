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
}
