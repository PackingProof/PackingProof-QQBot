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

    [Theory]
    [InlineData("GROUP_AT_MESSAGE_CREATE", "{\"id\":\"group-message\",\"content\":\"SF123456\",\"group_openid\":\"group-openid\"}", true, "group-openid")]
    [InlineData("C2C_MESSAGE_CREATE", "{\"id\":\"private-message\",\"content\":\"SF123456\",\"author\":{\"user_openid\":\"user-openid\"}}", false, "user-openid")]
    public void TryCreateIncomingMessage_RecognizesSupportedConversation(string eventType, string json, bool isGroup, string recipientOpenid)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        QQIncomingMessage? message = QQClient.TryCreateIncomingMessage(eventType, document.RootElement);

        Assert.NotNull(message);
        Assert.Equal("SF123456", message.Content);
        Assert.Equal(isGroup, message.IsGroup);
        Assert.Equal(recipientOpenid, message.RecipientOpenid);
    }

    [Fact]
    public void CalculateUploadOffsets_UsesActualPreviousPartSizes()
    {
        long[] offsets = QQClient.CalculateUploadOffsets([5 * 1024 * 1024, 5 * 1024 * 1024, 2 * 1024 * 1024]);

        Assert.Equal(new long[] { 0, 5L * 1024 * 1024, 10L * 1024 * 1024 }, offsets);
    }
}
