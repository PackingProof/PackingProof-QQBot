using System.Security.Cryptography;
using System.Text.Json;

namespace PackingProof.QQBot;

public sealed record QQBotConfiguration
{
    public const int DefaultDeliveryMaxSizeMb = 190;
    public const int MinimumDeliveryMaxSizeMb = 1;
    public const int MaximumDeliveryMaxSizeMb = 200;
    public const string SourceCodecTargetSizeProfile = "source_codec_target_size";
    public const string H265TargetSizeProfile = "h265_target_size";

    public string AppId { get; init; } = "";
    public string PackingProofBaseUrl { get; init; } = "http://127.0.0.1:5280";
    public string PackingProofNodeId { get; init; } = "";
    public string PackingProofNodeName { get; init; } = "";
    public string ExtensionInstanceId { get; init; } = "";
    public string[] AllowedGroupOpenIds { get; init; } = [];
    public QQKnownGroup[] KnownGroups { get; init; } = [];
    public int DeliveryMaxSizeMb { get; init; } = DefaultDeliveryMaxSizeMb;
    public string DeliveryProfile { get; init; } = SourceCodecTargetSizeProfile;
    public bool StartWithWindows { get; init; }

    public QQBotConfiguration ValidateDeliverySettings()
    {
        if (DeliveryMaxSizeMb is < MinimumDeliveryMaxSizeMb or > MaximumDeliveryMaxSizeMb)
            throw new InvalidDataException($"deliveryMaxSizeMb 必须在 {MinimumDeliveryMaxSizeMb} 到 {MaximumDeliveryMaxSizeMb} 之间");
        string profile = DeliveryProfile?.Trim().ToLowerInvariant() ?? "";
        if (profile is not (SourceCodecTargetSizeProfile or H265TargetSizeProfile))
            throw new InvalidDataException("deliveryProfile 必须是 source_codec_target_size 或 h265_target_size");
        return this with { DeliveryProfile = profile };
    }
}

public sealed record QQKnownGroup
{
    public string OpenId { get; init; } = "";
    public DateTimeOffset FirstSeenAtUtc { get; init; }
    public DateTimeOffset LastSeenAtUtc { get; init; }
}

public sealed record QQBotSecrets
{
    public string AppSecret { get; init; } = "";
    public ExtensionCredentialState? ExtensionCredential { get; init; }
}

public sealed record ExtensionCredentialState
{
    public string ExtensionInstanceId { get; init; } = "";
    public string Credential { get; init; } = "";
    public int CredentialGeneration { get; init; }
}

public sealed class QQBotStateStore
{
    private static readonly byte[] Entropy = "PackingProof.QQBot.v1"u8.ToArray();
    private static readonly byte[] LegacyEntropy = "PackingProof.QqBot.v1"u8.ToArray();
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public QQBotStateStore(string? directory = null) => _directory = directory ?? ResolveDefaultDirectory();

    public string DirectoryPath => _directory;
    public event Action? GroupsChanged;
    public QQBotConfiguration? LoadConfiguration()
    {
        lock (_gate) return LoadCore<QQBotConfiguration>("settings.json", false);
    }
    public QQBotSecrets? LoadSecrets()
    {
        lock (_gate) return LoadCore<QQBotSecrets>("secrets.dat", true);
    }

    public void Save(QQBotConfiguration configuration, QQBotSecrets secrets)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            SaveConfigurationCore(configuration);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(secrets, _jsonOptions);
            File.WriteAllBytes(Path.Combine(_directory, "secrets.dat"), ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
        }
    }

    public QQBotConfiguration RecordGroupSeen(string openId, DateTimeOffset seenAtUtc)
    {
        string normalized = NormalizeGroupOpenId(openId);
        QQBotConfiguration configuration;
        bool added;
        lock (_gate)
        {
            configuration = LoadCore<QQBotConfiguration>("settings.json", false)
                ?? throw new InvalidOperationException("请先保存并授权");
            QQKnownGroup? existing = configuration.KnownGroups
                .FirstOrDefault(group => string.Equals(group.OpenId, normalized, StringComparison.Ordinal));
            added = existing == null;
            var updated = new QQKnownGroup
            {
                OpenId = normalized,
                FirstSeenAtUtc = existing?.FirstSeenAtUtc ?? seenAtUtc,
                LastSeenAtUtc = seenAtUtc
            };
            configuration = configuration with
            {
                KnownGroups = configuration.KnownGroups
                    .Where(group => !string.Equals(group.OpenId, normalized, StringComparison.Ordinal))
                    .Append(updated)
                    .OrderByDescending(group => group.LastSeenAtUtc)
                    .ToArray()
            };
            SaveConfigurationCore(configuration);
        }
        GroupsChanged?.Invoke();
        if (added) QQBotLog.Write($"发现新的 QQ 群，已显示在管理器：{normalized}");
        return configuration;
    }

    public QQBotConfiguration SetGroupAllowed(string openId, bool allowed)
    {
        string normalized = NormalizeGroupOpenId(openId);
        QQBotConfiguration configuration;
        lock (_gate)
        {
            configuration = LoadCore<QQBotConfiguration>("settings.json", false)
                ?? throw new InvalidOperationException("请先保存并授权");
            IEnumerable<string> groups = allowed
                ? configuration.AllowedGroupOpenIds.Append(normalized)
                : configuration.AllowedGroupOpenIds.Where(group => !string.Equals(group, normalized, StringComparison.Ordinal));
            QQKnownGroup[] knownGroups = configuration.KnownGroups.Any(group => string.Equals(group.OpenId, normalized, StringComparison.Ordinal))
                ? configuration.KnownGroups
                : configuration.KnownGroups.Append(new QQKnownGroup
                {
                    OpenId = normalized,
                    FirstSeenAtUtc = DateTimeOffset.UtcNow,
                    LastSeenAtUtc = DateTimeOffset.UtcNow
                }).ToArray();
            configuration = configuration with
            {
                AllowedGroupOpenIds = groups.Distinct(StringComparer.Ordinal).Order().ToArray(),
                KnownGroups = knownGroups
            };
            SaveConfigurationCore(configuration);
        }
        GroupsChanged?.Invoke();
        return configuration;
    }

    private void SaveConfigurationCore(QQBotConfiguration configuration)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), JsonSerializer.Serialize(configuration, _jsonOptions));
    }

    private T? LoadCore<T>(string fileName, bool protectedFile)
    {
        string path = Path.Combine(_directory, fileName);
        if (!File.Exists(path)) return default;
        byte[] data = File.ReadAllBytes(path);
        if (protectedFile)
        {
            try { data = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { data = ProtectedData.Unprotect(data, LegacyEntropy, DataProtectionScope.CurrentUser); }
        }
        return JsonSerializer.Deserialize<T>(data, _jsonOptions);
    }

    private static string NormalizeGroupOpenId(string openId)
    {
        string normalized = openId?.Trim() ?? "";
        if (normalized.Length is < 1 or > 256 || normalized.Any(char.IsControl))
            throw new InvalidDataException("群 OpenID 格式无效");
        return normalized;
    }

    private static string ResolveDefaultDirectory()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExpressPackingMonitoring");
        string current = Path.Combine(root, "QQBot");
        string legacy = Path.Combine(root, "QqBot");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }
}
