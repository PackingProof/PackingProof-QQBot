using System.Net;
using System.Text;
using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class PackingProofClientRecoveryTests
{
    [Fact]
    public async Task CreateQueryAsync_RetriesOnceWithRecoveredSameNodeConfiguration()
    {
        var handler = new RecoveryHandler();
        using var http = new HttpClient(handler);
        var oldConfiguration = new QQBotConfiguration { PackingProofBaseUrl = "http://old-host:5280" };
        var recoveredConfiguration = oldConfiguration with { PackingProofBaseUrl = "http://new-host:5280" };
        var credential = new ExtensionCredentialState { ExtensionInstanceId = "qqbot-test", Credential = new string('a', 64), CredentialGeneration = 1 };
        var client = new PackingProofClient(http, oldConfiguration, credential, _ => Task.FromResult<QQBotConfiguration?>(recoveredConfiguration));

        RecordingQuery query = await client.CreateQueryAsync("SF1234567890", CancellationToken.None);

        Assert.Equal("ready", query.Status);
        Assert.Equal(["old-host", "new-host"], handler.Hosts);
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "old-host") throw new HttpRequestException("主机地址已变化");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"queryId\":\"query-1\",\"status\":\"ready\",\"recordings\":[]}", Encoding.UTF8, "application/json")
            });
        }
    }
}
