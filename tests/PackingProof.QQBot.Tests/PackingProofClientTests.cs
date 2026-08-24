using System.Net;
using System.Text;
using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class PackingProofClientTests
{
    [Fact]
    public async Task EnrollAsync_ReportsUnsupportedEndpointInsteadOfDisabledApi()
    {
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.NotFound, "{}"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackingProofClient.EnrollAsync(http, new QQBotConfiguration(), CancellationToken.None));

        Assert.Contains("未提供扩展 API", exception.Message);
        Assert.DoesNotContain("尚未启用", exception.Message);
    }

    [Fact]
    public async Task EnrollAsync_ReportsDisabledApiOnlyWhenCapabilitySaysDisabled()
    {
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, "{\"extensionApiEnabled\":false,\"features\":{}}"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackingProofClient.EnrollAsync(http, new QQBotConfiguration(), CancellationToken.None));

        Assert.Equal("PackingProof 尚未启用扩展 API", exception.Message);
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
