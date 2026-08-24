using System.Net;
using System.Text;
using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class PackingProofHostDiscoveryTests
{
    private const string NodeId = "1e2f3a4b-5c6d-4789-8abc-9def01234567";

    [Fact]
    public async Task ProbeAsync_AcceptsOnlySupportedPackingProofHost()
    {
        using var http = new HttpClient(new NodeInfoHandler(new Dictionary<string, string>
        {
            ["http://host:5280"] = Node(NodeId, 5290)
        }));

        PackingProofHostInfo? host = await new PackingProofHostDiscovery(http).ProbeAsync("http://host:5280", CancellationToken.None);

        Assert.NotNull(host);
        Assert.Equal("http://host:5290", host.BaseUrl);
        Assert.Equal(NodeId, host.NodeId);
    }

    [Theory]
    [InlineData("other", 1, 5280, true)]
    [InlineData("packingproof", 2, 5280, true)]
    [InlineData("packingproof", 1, 0, true)]
    [InlineData("packingproof", 1, 5280, false)]
    public async Task ProbeAsync_RejectsWrongProtocolPortOrCapability(string protocol, int version, int port, bool includesHost)
    {
        using var http = new HttpClient(new NodeInfoHandler(new Dictionary<string, string>
        {
            ["http://host:5280"] = Node(NodeId, port, protocol, version, includesHost)
        }));

        PackingProofHostInfo? host = await new PackingProofHostDiscovery(http).ProbeAsync("http://host:5280", CancellationToken.None);

        Assert.Null(host);
    }

    [Fact]
    public async Task FindByNodeIdAsync_PrefersSavedAddressBeforeScanning()
    {
        using var http = new HttpClient(new NodeInfoHandler(new Dictionary<string, string>
        {
            ["http://saved:5280"] = Node(NodeId),
            ["http://candidate:5280"] = Node(NodeId)
        }));

        PackingProofHostInfo? host = await new PackingProofHostDiscovery(http, ["http://candidate:5280"]).FindByNodeIdAsync(NodeId, "http://saved:5280", CancellationToken.None);

        Assert.NotNull(host);
        Assert.Equal("http://saved:5280", host.BaseUrl);
    }

    [Fact]
    public async Task FindByNodeIdAsync_RecoversChangedAddressOnlyForSameNode()
    {
        using var http = new HttpClient(new NodeInfoHandler(new Dictionary<string, string>
        {
            ["http://new-host:5280"] = Node(NodeId)
        }));

        PackingProofHostInfo? host = await new PackingProofHostDiscovery(http, ["http://new-host:5280"]).FindByNodeIdAsync(NodeId, "http://old-host:5280", CancellationToken.None);

        Assert.NotNull(host);
        Assert.Equal("http://new-host:5280", host.BaseUrl);
    }

    [Fact]
    public async Task FindByNodeIdAsync_RejectsOtherPackingProofNode()
    {
        using var http = new HttpClient(new NodeInfoHandler(new Dictionary<string, string>
        {
            ["http://other-host:5280"] = Node("5f6e7d8c-9b0a-4123-8456-7890abcdef12")
        }));

        PackingProofHostInfo? host = await new PackingProofHostDiscovery(http, ["http://other-host:5280"]).FindByNodeIdAsync(NodeId, "http://old-host:5280", CancellationToken.None);

        Assert.Null(host);
    }

    [Fact]
    public async Task ResolvePackingProofHostAsync_AddsNodeIdForLegacyConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PackingProof-QQBot-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new QQBotStateStore(directory);
            var configuration = new QQBotConfiguration { AppId = "app", PackingProofBaseUrl = "http://host:5280", ExtensionInstanceId = "qqbot-test" };
            var secrets = new QQBotSecrets { AppSecret = "secret" };
            store.Save(configuration, secrets);
            using var http = new HttpClient(new NodeInfoHandler(new Dictionary<string, string> { ["http://host:5280"] = Node(NodeId) }));

            QQBotConfiguration resolved = await Program.ResolvePackingProofHostAsync(store, configuration, secrets, http, CancellationToken.None);

            Assert.Equal(NodeId, resolved.PackingProofNodeId);
            Assert.Equal(NodeId, store.LoadConfiguration()!.PackingProofNodeId);
            Assert.Equal("Test host", store.LoadConfiguration()!.PackingProofNodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string Node(string nodeId, int port = 5280, string protocol = "packingproof", int version = 1, bool includesHost = true)
    {
        string capability = includesHost ? "host" : "viewer";
        return $$"""{"protocol":"{{protocol}}","protocolVersion":{{version}},"nodeId":"{{nodeId}}","nodeName":"Test host","preset":"default","capabilities":["{{capability}}"],"httpPort":{{port}}}""";
    }

    private sealed class NodeInfoHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string baseUrl = request.RequestUri!.GetLeftPart(UriPartial.Authority);
            return Task.FromResult(responses.TryGetValue(baseUrl, out string? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
