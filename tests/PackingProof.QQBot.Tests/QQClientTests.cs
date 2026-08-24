using System.Text.Json;
using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class QQClientTests
{
    [Theory]
    [InlineData("10")]
    [InlineData("\"10\"")]
    public void ReadRequiredInt32_AcceptsNumberAndNumericString(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        int value = QQClient.ReadRequiredInt32(document.RootElement, "测试字段");

        Assert.Equal(10, value);
    }

    [Fact]
    public void ReadRequiredInt32_RejectsNonNumericString()
    {
        using JsonDocument document = JsonDocument.Parse("\"invalid\"");

        Assert.Throws<InvalidDataException>(() => QQClient.ReadRequiredInt32(document.RootElement, "测试字段"));
    }
}
