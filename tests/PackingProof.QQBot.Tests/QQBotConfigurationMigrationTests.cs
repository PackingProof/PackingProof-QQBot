using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class QQBotConfigurationMigrationTests
{
    [Fact]
    public void CreateConfiguration_ReauthorizationPreservesUserSettings()
    {
        var existing = new QQBotConfiguration
        {
            ExtensionInstanceId = "qqbot-existing",
            AllowedGroupOpenIds = ["group-a"],
            KnownGroups = [new QQKnownGroup { OpenId = "group-a", FirstSeenAtUtc = DateTimeOffset.UnixEpoch, LastSeenAtUtc = DateTimeOffset.UnixEpoch }],
            DeliveryMaxSizeMb = 150,
            DeliveryProfile = QQBotConfiguration.H265TargetSizeProfile,
            StartWithWindows = true
        };
        var host = new PackingProofHostInfo
        {
            NodeId = "1e2f3a4b-5c6d-4789-8abc-9def01234567",
            NodeName = "新的打包主机",
            BaseUrl = "http://192.168.1.20:5280"
        };

        QQBotConfiguration configuration = Program.CreateConfiguration(existing, "new-app-id", host);

        Assert.Equal("new-app-id", configuration.AppId);
        Assert.Equal("qqbot-existing", configuration.ExtensionInstanceId);
        Assert.Equal(["group-a"], configuration.AllowedGroupOpenIds);
        Assert.Equal(["group-a"], configuration.KnownGroups.Select(group => group.OpenId));
        Assert.Equal(150, configuration.DeliveryMaxSizeMb);
        Assert.Equal(QQBotConfiguration.H265TargetSizeProfile, configuration.DeliveryProfile);
        Assert.True(configuration.StartWithWindows);
        Assert.Equal("新的打包主机", configuration.PackingProofNodeName);
    }

    /// <summary>
    /// 老版本把配置放在 QQBot\，规范位置是 extension-data\packingproof.qqbot。
    /// 迁移只复制 settings.json / secrets.dat；旧文件保留（自启动还指着旧版本目录时，旧版本仍能读）。
    /// </summary>
    [Fact]
    public void StateStore_MigratesLegacyConfigurationToTheExtensionDataDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "PackingProof-QQBot-Migrate-" + Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "QQBot");
        string target = Path.Combine(root, "extension-data", "packingproof.qqbot");
        try
        {
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), """{"appId":"legacy-app"}""");
            File.WriteAllBytes(Path.Combine(legacy, "secrets.dat"), [1, 2, 3]);

            QQBotStateStore.MigrateLegacyState(root, target);

            Assert.True(File.Exists(Path.Combine(target, "settings.json")));
            Assert.True(File.Exists(Path.Combine(target, "secrets.dat")));
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")), "旧文件保留，旧版本/回滚还能读");
            Assert.True(File.Exists(Path.Combine(legacy, "secrets.dat")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>新目录里已经有配置时（用户后来在新版本里改过）绝不能被旧文件覆盖。</summary>
    [Fact]
    public void StateStore_DoesNotOverwriteConfigurationAlreadyInTheNewDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "PackingProof-QQBot-Migrate-" + Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "QQBot");
        string target = Path.Combine(root, "extension-data", "packingproof.qqbot");
        try
        {
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), """{"appId":"old"}""");
            File.WriteAllText(Path.Combine(target, "settings.json"), """{"appId":"new"}""");

            QQBotStateStore.MigrateLegacyState(root, target);

            Assert.Equal("""{"appId":"new"}""", File.ReadAllText(Path.Combine(target, "settings.json")));
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>桌面端注入的数据目录优先；没有注入时按规范自行推导，两种启动方式必须指向同一份数据。</summary>
    [Fact]
    public void ResolveDirectory_UsesInjectedDirectoryOrTheCanonicalOne()
    {
        Assert.Equal(
            Path.GetFullPath(@"C:\inject\extension-data"),
            QQBotStateStore.ResolveDirectory(@"C:\inject\extension-data", @"C:\Users\Test\AppData\Local"));

        Assert.Equal(
            Path.Combine(@"C:\Users\Test\AppData\Local", "ExpressPackingMonitoring", "extension-data", "packingproof.qqbot"),
            QQBotStateStore.ResolveDirectory("", @"C:\Users\Test\AppData\Local"));
    }

    [Fact]
    public void StateStore_RecordsSeenGroupsAndAppliesPermissionWithoutRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PackingProof-QQBot-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new QQBotStateStore(directory);
            store.Save(new QQBotConfiguration { AppId = "app" }, new QQBotSecrets { AppSecret = "secret" });
            DateTimeOffset seenAt = new(2026, 8, 26, 8, 30, 0, TimeSpan.Zero);

            QQBotConfiguration seen = store.RecordGroupSeen("group-new", seenAt);
            QQBotConfiguration allowed = store.SetGroupAllowed("group-new", true);

            QQKnownGroup group = Assert.Single(seen.KnownGroups);
            Assert.Equal("group-new", group.OpenId);
            Assert.Equal(seenAt, group.FirstSeenAtUtc);
            Assert.Equal(seenAt, group.LastSeenAtUtc);
            Assert.Contains("group-new", allowed.AllowedGroupOpenIds);
            Assert.Contains("group-new", store.LoadConfiguration()!.AllowedGroupOpenIds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BuildGroupItems_IncludesDiscoveredAndLegacyAllowedGroups()
    {
        DateTimeOffset seenAt = new(2026, 8, 26, 8, 30, 0, TimeSpan.Zero);
        var configuration = new QQBotConfiguration
        {
            AllowedGroupOpenIds = ["group-legacy"],
            KnownGroups = [new QQKnownGroup { OpenId = "group-new", FirstSeenAtUtc = seenAt, LastSeenAtUtc = seenAt }]
        };

        QQGroupDisplayItem[] items = QQBotManagerWindow.BuildGroupItems(configuration);

        Assert.Equal(2, items.Length);
        Assert.False(items.Single(item => item.OpenId == "group-new").IsAllowed);
        Assert.True(items.Single(item => item.OpenId == "group-legacy").IsAllowed);
    }
}
