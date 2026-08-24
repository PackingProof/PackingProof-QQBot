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

    [Fact]
    public void BuildRecordingSummary_ShowsCountDateDurationSizeAndContinuePromptBeforeUpload()
    {
        var recordings = new[]
        {
            new Recording
            {
                RecordingId = 1,
                RecordedAt = new DateTime(2026, 8, 24, 23, 37, 0),
                DurationSeconds = 65,
                FileSizeBytes = 12L * 1024 * 1024,
                Status = "ready",
                DownloadUrl = "/download"
            },
            new Recording
            {
                RecordingId = 2,
                RecordedAt = new DateTime(2026, 8, 24, 23, 39, 0),
                DurationSeconds = 120,
                FileSizeBytes = 220L * 1024 * 1024,
                Status = "ready",
                DownloadUrl = "/download"
            }
        };
        var query = new RecordingQuery { TotalMatches = 3, Recordings = recordings };

        string summary = QueryService.BuildRecordingSummary("6974412900385", query, recordings, 190, remainingCount: 1);

        Assert.Contains("找到 3 段录像，本次发送 2 段，还剩 1 段。回复“继续”即可发送下一批", summary, StringComparison.Ordinal);
        Assert.Contains("08-24 23:37｜1:05｜12.0 MB｜准备发送原片", summary, StringComparison.Ordinal);
        Assert.Contains("08-24 23:39｜2:00｜220.0 MB｜将生成交付副本后发送", summary, StringComparison.Ordinal);
    }
}
