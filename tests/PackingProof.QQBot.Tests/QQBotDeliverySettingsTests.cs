using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class QQBotDeliverySettingsTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("200", 200)]
    [InlineData("  190  ", 190)]
    public void ValidateDeliverySizeInput_AcceptsRange(string text, int expected)
    {
        string? error = QQBotManagerWindow.ValidateDeliverySizeInput(text, out int size);

        Assert.Null(error);
        Assert.Equal(expected, size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("-1")]
    public void ValidateDeliverySizeInput_RejectsInvalidValuesWithoutThrowing(string text)
    {
        string? error = QQBotManagerWindow.ValidateDeliverySizeInput(text, out _);

        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ValidateDeliverySizeInput_OutOfRangeMessageExplainsBounds()
    {
        string? error = QQBotManagerWindow.ValidateDeliverySizeInput("201", out _);

        Assert.Contains("1 到 200", error);
    }
}
