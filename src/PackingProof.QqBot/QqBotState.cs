using System.Security.Cryptography;
using System.Text.Json;

namespace PackingProof.QqBot;

public sealed record QqBotConfiguration
{
    public const int DefaultDeliveryMaxSizeMb = 190;
    public const int MinimumDeliveryMaxSizeMb = 1;
    public const int MaximumDeliveryMaxSizeMb = 200;
    public const string SourceCodecTargetSizeProfile = "source_codec_target_size";
    public const string H265TargetSizeProfile = "h265_target_size";

    public string AppId { get; init; } = "";
    public string PackingProofBaseUrl { get; init; } = "http://127.0.0.1:5280";
    public string ExtensionInstanceId { get; init; } = "";
    public string[] AllowedGroupOpenIds { get; init; } = [];
    public int DeliveryMaxSizeMb { get; init; } = DefaultDeliveryMaxSizeMb;
    public string DeliveryProfile { get; init; } = SourceCodecTargetSizeProfile;

    public QqBotConfiguration ValidateDeliverySettings()
    {
        if (DeliveryMaxSizeMb is < MinimumDeliveryMaxSizeMb or > MaximumDeliveryMaxSizeMb)
            throw new InvalidDataException($"deliveryMaxSizeMb 必须在 {MinimumDeliveryMaxSizeMb} 到 {MaximumDeliveryMaxSizeMb} 之间");
        string profile = DeliveryProfile?.Trim().ToLowerInvariant() ?? "";
        if (profile is not (SourceCodecTargetSizeProfile or H265TargetSizeProfile))
            throw new InvalidDataException("deliveryProfile 必须是 source_codec_target_size 或 h265_target_size");
        return this with { DeliveryProfile = profile };
    }
}

public sealed record QqBotSecrets
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

public sealed class QqBotStateStore
{
    private static readonly byte[] Entropy = "PackingProof.QqBot.v1"u8.ToArray();
    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public QqBotStateStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExpressPackingMonitoring", "QqBot");

    public string DirectoryPath => _directory;
    public QqBotConfiguration? LoadConfiguration() => Load<QqBotConfiguration>("settings.json", false);
    public QqBotSecrets? LoadSecrets() => Load<QqBotSecrets>("secrets.dat", true);

    public void Save(QqBotConfiguration configuration, QqBotSecrets secrets)
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
        if (protectedFile) data = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<T>(data, _jsonOptions);
    }
}
