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
    public string ExtensionInstanceId { get; init; } = "";
    public string[] AllowedGroupOpenIds { get; init; } = [];
    public int DeliveryMaxSizeMb { get; init; } = DefaultDeliveryMaxSizeMb;
    public string DeliveryProfile { get; init; } = SourceCodecTargetSizeProfile;

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
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public QQBotStateStore(string? directory = null) => _directory = directory ?? ResolveDefaultDirectory();

    public string DirectoryPath => _directory;
    public QQBotConfiguration? LoadConfiguration() => Load<QQBotConfiguration>("settings.json", false);
    public QQBotSecrets? LoadSecrets() => Load<QQBotSecrets>("secrets.dat", true);

    public void Save(QQBotConfiguration configuration, QQBotSecrets secrets)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), JsonSerializer.Serialize(configuration, _jsonOptions));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(secrets, _jsonOptions);
        File.WriteAllBytes(Path.Combine(_directory, "secrets.dat"), ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
    }

    private T? Load<T>(string fileName, bool protectedFile)
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

    private static string ResolveDefaultDirectory()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExpressPackingMonitoring");
        string current = Path.Combine(root, "QQBot");
        string legacy = Path.Combine(root, "QqBot");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }
}
