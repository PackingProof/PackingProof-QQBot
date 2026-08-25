using System.Text.Json;
using System.Net;
using System.Text;
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

    [Fact]
    public async Task FirstGroupMessage_IsRecordedAndReceivesPermissionPromptWithoutPrivateMessage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PackingProof-QQBot-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new QQBotConfiguration
            {
                AppId = "app",
                PackingProofBaseUrl = "http://packingproof:5280",
                ExtensionInstanceId = "qqbot-test"
            };
            var secrets = new QQBotSecrets { AppSecret = "secret" };
            var store = new QQBotStateStore(directory);
            store.Save(configuration, secrets);
            var handler = new QQReplyHandler();
            using var http = new HttpClient(handler);
            var qq = new QQClient(http, configuration, secrets);
            var packingProof = new PackingProofClient(http, configuration, new ExtensionCredentialState
            {
                ExtensionInstanceId = "qqbot-test",
                Credential = new string('a', 64),
                CredentialGeneration = 1
            });
            var service = new QueryService(configuration, packingProof, qq, store);

            await service.HandleAsync(
                new QQIncomingMessage("message-1", "SF123456", "group-first", true),
                CancellationToken.None);

            Assert.Contains(store.LoadConfiguration()!.KnownGroups, group => group.OpenId == "group-first");
            Assert.Contains("这个群尚未允许使用", handler.LastMessageBody, StringComparison.Ordinal);
            Assert.Equal(1, handler.GroupReplyCount);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AllowedGroupQuery_ImmediatelyAcknowledgesBeforeResultWithUniqueSequences()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PackingProof-QQBot-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new QQBotConfiguration
            {
                AppId = "app",
                PackingProofBaseUrl = "http://packingproof:5280",
                ExtensionInstanceId = "qqbot-test",
                AllowedGroupOpenIds = ["group-first"]
            };
            var secrets = new QQBotSecrets { AppSecret = "secret" };
            var store = new QQBotStateStore(directory);
            store.Save(configuration, secrets);
            var handler = new QQReplyHandler();
            using var http = new HttpClient(handler);
            var qq = new QQClient(http, configuration, secrets);
            var packingProof = new PackingProofClient(http, configuration, new ExtensionCredentialState
            {
                ExtensionInstanceId = "qqbot-test",
                Credential = new string('a', 64),
                CredentialGeneration = 1
            });
            var service = new QueryService(configuration, packingProof, qq, store);

            await service.HandleAsync(
                new QQIncomingMessage("message-2", "<@!bot-id> SF123456", "group-first", true),
                CancellationToken.None);

            Assert.Equal(2, handler.GroupMessageBodies.Count);
            using JsonDocument acknowledgement = JsonDocument.Parse(handler.GroupMessageBodies[0]);
            using JsonDocument result = JsonDocument.Parse(handler.GroupMessageBodies[1]);
            Assert.Equal("正在查询单号 SF123456 的录像", acknowledgement.RootElement.GetProperty("content").GetString());
            Assert.Equal(1, acknowledgement.RootElement.GetProperty("msg_seq").GetInt32());
            Assert.Contains("未找到单号 SF123456", result.RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.Equal(2, result.RootElement.GetProperty("msg_seq").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class QQReplyHandler : HttpMessageHandler
    {
        public int GroupReplyCount { get; private set; }
        public string LastMessageBody { get; private set; } = "";
        public List<string> GroupMessageBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/app/getAppAccessToken", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{\"access_token\":\"token\",\"expires_in\":3600}");
            }

            if (request.RequestUri.AbsolutePath == "/v2/groups/group-first/messages")
            {
                GroupReplyCount++;
                string body = request.Content == null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                GroupMessageBodies.Add(body);
                using JsonDocument payload = JsonDocument.Parse(body);
                LastMessageBody = payload.RootElement.GetProperty("content").GetString() ?? "";
                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.RequestUri.AbsolutePath == "/api/extensions/v1/recording-queries")
                return Json(HttpStatusCode.OK, "{\"queryId\":\"query-1\",\"status\":\"not_found\",\"recordings\":[]}");

            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
