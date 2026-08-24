using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PackingProof.QQBot;

internal sealed record PackingProofHostInfo
{
    public const string ExpectedProtocol = "packingproof";
    public const int SupportedProtocolVersion = 1;

    public string Protocol { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public string NodeId { get; init; } = "";
    public string NodeName { get; init; } = "";
    public string Preset { get; init; } = "";
    public string[] Capabilities { get; init; } = [];
    public int HttpPort { get; init; }
    public string BaseUrl { get; init; } = "";

    public bool IsValidHost =>
        string.Equals(Protocol, ExpectedProtocol, StringComparison.Ordinal)
        && ProtocolVersion == SupportedProtocolVersion
        && Guid.TryParse(NodeId, out Guid nodeId)
        && nodeId != Guid.Empty
        && HttpPort is > 0 and <= 65535
        && Capabilities.Contains("host", StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 与 PackingProof Desktop 的 WorkstationNetwork 保持相同协议：先验证旧地址，
/// 再通过 UDP 广播和受限 IPv4 子网扫描寻找相同 nodeId 的主机。
/// </summary>
internal sealed class PackingProofHostDiscovery(HttpClient http)
{
    private const int DefaultHttpPort = 5280;
    private const int UdpPort = 5281;
    private const int MaxPacketBytes = 512;
    private const int MaxSubnetHosts = 1022;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = http;

    public async Task<PackingProofHostInfo?> ProbeAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out Uri? uri)) return null;
        Uri verifiedUri = uri!;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(new Uri(verifiedUri, "/api/node-info"), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            PackingProofHostInfo? node = await response.Content.ReadFromJsonAsync<PackingProofHostInfo>(cancellationToken: cancellationToken);
            if (node is not { IsValidHost: true } verified) return null;
            return verified with { BaseUrl = new UriBuilder(verifiedUri.Scheme, verifiedUri.Host, verified.HttpPort).Uri.AbsoluteUri.TrimEnd('/') };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    public async Task<PackingProofHostInfo?> FindByNodeIdAsync(string nodeId, string lastKnownBaseUrl, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(nodeId, out Guid expectedNodeId) || expectedNodeId == Guid.Empty) return null;
        PackingProofHostInfo? saved = await ProbeAsync(lastKnownBaseUrl, cancellationToken);
        if (IsMatching(saved, nodeId)) return saved;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Task<PackingProofHostInfo?> udp = FindByUdpAsync(nodeId, timeout.Token);
        Task<PackingProofHostInfo?> subnet = FindBySubnetAsync(nodeId, timeout.Token);
        try { await Task.WhenAll(udp, subnet); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return udp.Status == TaskStatus.RanToCompletion && udp.Result != null
            ? udp.Result
            : subnet.Status == TaskStatus.RanToCompletion ? subnet.Result : null;
    }

    public async Task<IReadOnlyList<PackingProofHostInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var hosts = new Dictionary<string, PackingProofHostInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (UdpAnnounce announce in DiscoverUdpAsync(timeout.Token))
            {
                PackingProofHostInfo? host = await ProbeAsync($"http://{announce.SourceIp}:{announce.HttpPort}", timeout.Token);
                if (host != null) hosts[host.BaseUrl] = host;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        string[] candidates = GetLocalCandidates().Select(address => $"http://{address}:{DefaultHttpPort}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        for (int index = 0; index < candidates.Length && !timeout.IsCancellationRequested; index += 32)
        {
            PackingProofHostInfo?[] results;
            try { results = await Task.WhenAll(candidates.Skip(index).Take(32).Select(address => ProbeAsync(address, timeout.Token))); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            foreach (PackingProofHostInfo? host in results.Where(host => host != null)) hosts[host!.BaseUrl] = host!;
        }
        return hosts.Values.OrderBy(host => host.NodeName, StringComparer.OrdinalIgnoreCase).ThenBy(host => host.BaseUrl, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<PackingProofHostInfo?> FindByUdpAsync(string nodeId, CancellationToken cancellationToken)
    {
        await foreach (UdpAnnounce announce in DiscoverUdpAsync(cancellationToken))
        {
            if (!string.Equals(announce.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)) continue;
            PackingProofHostInfo? host = await ProbeAsync($"http://{announce.SourceIp}:{announce.HttpPort}", cancellationToken);
            if (IsMatching(host, nodeId)) return host;
        }
        return null;
    }

    private async Task<PackingProofHostInfo?> FindBySubnetAsync(string nodeId, CancellationToken cancellationToken)
    {
        string[] candidates = GetLocalCandidates()
            .SelectMany(address => new[] { $"http://{address}:{DefaultHttpPort}" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (int index = 0; index < candidates.Length; index += 32)
        {
            PackingProofHostInfo?[] results = await Task.WhenAll(candidates.Skip(index).Take(32).Select(address => ProbeAsync(address, cancellationToken)));
            PackingProofHostInfo? match = results.FirstOrDefault(host => IsMatching(host, nodeId));
            if (match != null) return match;
        }
        return null;
    }

    private static bool IsMatching(PackingProofHostInfo? host, string nodeId) => host?.IsValidHost == true && string.Equals(host.NodeId, nodeId, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeBaseUrl(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? candidate)
            || candidate.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(candidate.Host)) return false;
        uri = candidate;
        return true;
    }

    private static IEnumerable<string> GetLocalCandidates()
    {
        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (UnicastIPAddressInformation address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address.Address)) continue;
                foreach (IPAddress candidate in EnumerateSubnet(address.Address, address.IPv4Mask)) yield return candidate.ToString();
            }
        }
    }

    private static IEnumerable<IPAddress> EnumerateSubnet(IPAddress address, IPAddress? mask)
    {
        byte[] bytes = address.GetAddressBytes();
        byte[] maskBytes = mask?.GetAddressBytes() is { Length: 4 } maskValueBytes ? maskValueBytes : [255, 255, 255, 0];
        uint addressValue = ToUInt32(bytes);
        uint maskValue = ToUInt32(maskBytes);
        uint network = addressValue & maskValue;
        uint broadcast = network | ~maskValue;
        if (broadcast <= network || (ulong)broadcast - network - 1 > MaxSubnetHosts)
        {
            maskValue = 0xffffff00;
            network = addressValue & maskValue;
            broadcast = network | ~maskValue;
        }
        for (uint candidateValue = network + 1; candidateValue < broadcast; candidateValue++) yield return new IPAddress([(byte)(candidateValue >> 24), (byte)(candidateValue >> 16), (byte)(candidateValue >> 8), (byte)candidateValue]);
    }

    private static uint ToUInt32(byte[] bytes) => ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static async IAsyncEnumerable<UdpAnnounce> DiscoverUdpAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new { protocol = PackingProofHostInfo.ExpectedProtocol, protocolVersion = PackingProofHostInfo.SupportedProtocolVersion, action = "discover" });
        if (request.Length > MaxPacketBytes) yield break;
        try { await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort)); }
        catch (SocketException) { yield break; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(600));
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) { yield break; }
            if (result.Buffer.Length > MaxPacketBytes) continue;
            UdpAnnounce? announce = null;
            try
            {
                UdpPacket? packet = JsonSerializer.Deserialize<UdpPacket>(result.Buffer, JsonOptions);
                if (packet?.Protocol == PackingProofHostInfo.ExpectedProtocol
                    && packet.ProtocolVersion == PackingProofHostInfo.SupportedProtocolVersion
                    && packet.Action == "announce"
                    && Guid.TryParse(packet.NodeId, out Guid nodeId) && nodeId != Guid.Empty
                    && packet.HttpPort is > 0 and <= 65535)
                    announce = new UdpAnnounce(nodeId.ToString("D"), packet.HttpPort, result.RemoteEndPoint.Address.ToString());
            }
            catch (JsonException) { }
            if (announce != null) yield return announce;
        }
    }

    private sealed record UdpPacket(string Protocol, int ProtocolVersion, string Action, string NodeId, int HttpPort);
    private sealed record UdpAnnounce(string NodeId, int HttpPort, string SourceIp);
}
