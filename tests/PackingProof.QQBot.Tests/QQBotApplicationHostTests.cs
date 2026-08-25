namespace PackingProof.QQBot.Tests;

public sealed class QQBotApplicationHostTests
{
    [Fact]
    public void CanAutoStart_RequiresCompleteConfigurationAndSecrets()
    {
        var configuration = new QQBotConfiguration { AppId = "app-id" };
        var secrets = new QQBotSecrets
        {
            AppSecret = "secret",
            ExtensionCredential = new ExtensionCredentialState { Credential = "credential" }
        };

        Assert.True(QQBotApplicationHost.CanAutoStart(configuration, secrets));
        Assert.False(QQBotApplicationHost.CanAutoStart(null, secrets));
        Assert.False(QQBotApplicationHost.CanAutoStart(configuration with { AppId = "" }, secrets));
        Assert.False(QQBotApplicationHost.CanAutoStart(configuration, secrets with { AppSecret = "" }));
        Assert.False(QQBotApplicationHost.CanAutoStart(configuration, secrets with { ExtensionCredential = null }));
        Assert.False(QQBotApplicationHost.CanAutoStart(configuration, secrets with { ExtensionCredential = new ExtensionCredentialState() }));
    }

    [Theory]
    [InlineData(false, "启动机器人")]
    [InlineData(true, "停止机器人")]
    public void BotToggleText_FollowsRuntimeState(bool isRunning, string expected)
    {
        Assert.Equal(expected, QQBotManagerWindow.BotToggleText(isRunning));
    }
}
