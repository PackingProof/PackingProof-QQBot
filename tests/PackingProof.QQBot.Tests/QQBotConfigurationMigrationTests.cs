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
