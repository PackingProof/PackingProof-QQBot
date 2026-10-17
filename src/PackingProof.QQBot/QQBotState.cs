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

    public QQBotStateStore(string? directory = null)
    {
        _directory = directory ?? ResolveDefaultDirectory();
        // 只有"自己算出来的默认目录"才做老配置迁移；测试或调用方显式指定的目录一律不动。
        if (directory == null) MigrateLegacyState(ResolveLegacyRoot(), _directory);
    }

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

    /// <summary>
    /// 规范位置：桌面端启动扩展时注入 <c>PACKINGPROOF_EXTENSION_DATA_DIR</c>
    /// （<c>%LOCALAPPDATA%\ExpressPackingMonitoring\extension-data\&lt;id&gt;</c>）。
    /// 手动运行或开机自启时没有这个变量，就按同一条约定自行推导——两种启动方式必须读写同一份数据，
    /// 不能一个看注入目录、一个看老目录。
    /// </summary>
    private static string ResolveDefaultDirectory() =>
        ResolveDirectory(
            Environment.GetEnvironmentVariable("PACKINGPROOF_EXTENSION_DATA_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string ResolveDirectory(string? overridePath, string localAppDataRoot)
    {
        string trimmed = (overridePath ?? "").Trim();
        if (trimmed.Length > 0) return Path.GetFullPath(trimmed);
        return Path.Combine(
            Path.GetFullPath(localAppDataRoot),
            "ExpressPackingMonitoring",
            "extension-data",
            "packingproof.qqbot");
    }

    /// <summary>
    /// 0.0.7 及以前把配置放在 <c>%LOCALAPPDATA%\ExpressPackingMonitoring\QQBot\</c>（更早是 <c>QqBot\</c>）。
    /// 迁到规范目录时只复制 <c>settings.json</c> 与 <c>secrets.dat</c>；
    /// 目标目录已经有配置就不动，避免覆盖用户后来改过的设置。旧文件**保留**：万一自启动项还指着
    /// 旧版本目录（自动更新后的典型情况），旧版本启动时仍能读到原来的配置，不会变成"未配置"。
    /// </summary>
    internal static void MigrateLegacyState(string legacyRoot, string targetDirectory)
    {
        try
        {
            string? legacyDirectory = new[] { "QQBot", "QqBot" }
                .Select(name => Path.Combine(legacyRoot, name))
                .FirstOrDefault(directory =>
                    File.Exists(Path.Combine(directory, "settings.json"))
                    || File.Exists(Path.Combine(directory, "secrets.dat")));
            if (legacyDirectory == null) return;

            string target = Path.GetFullPath(targetDirectory);
            if (string.Equals(Path.GetFullPath(legacyDirectory), target, StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(Path.Combine(target, "settings.json"))
                || File.Exists(Path.Combine(target, "secrets.dat")))
            {
                return;
            }

            Directory.CreateDirectory(target);
            foreach (string fileName in new[] { "settings.json", "secrets.dat" })
            {
                string source = Path.Combine(legacyDirectory, fileName);
                string destination = Path.Combine(target, fileName);
                if (File.Exists(source) && !File.Exists(destination))
                    File.Copy(source, destination);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 迁移失败不阻断启动：用户在界面上重新保存一次就会写到规范目录。
        }
    }

    private static string ResolveLegacyRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExpressPackingMonitoring");
}
