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
        Assert.Equal(150, configuration.DeliveryMaxSizeMb);
        Assert.Equal(QQBotConfiguration.H265TargetSizeProfile, configuration.DeliveryProfile);
        Assert.True(configuration.StartWithWindows);
        Assert.Equal("新的打包主机", configuration.PackingProofNodeName);
    }
}
