using PackingProof.QQBot;

namespace PackingProof.QQBot.Tests;

public sealed class QQBotConfigurationTests
{
    [Fact]
    public void ValidateDeliverySettings_UsesDefaultSourceCodecProfile()
    {
        QQBotConfiguration configuration = new QQBotConfiguration().ValidateDeliverySettings();

        Assert.Equal(190, configuration.DeliveryMaxSizeMb);
        Assert.Equal(QQBotConfiguration.SourceCodecTargetSizeProfile, configuration.DeliveryProfile);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void ValidateDeliverySettings_RejectsOutOfRangeSize(int size)
    {
        var configuration = new QQBotConfiguration { DeliveryMaxSizeMb = size };

        Assert.Throws<InvalidDataException>(() => configuration.ValidateDeliverySettings());
    }

    [Fact]
    public void ValidateDeliverySettings_AcceptsH265Profile()
    {
        var configuration = new QQBotConfiguration { DeliveryProfile = " H265_TARGET_SIZE " };

        Assert.Equal(QQBotConfiguration.H265TargetSizeProfile, configuration.ValidateDeliverySettings().DeliveryProfile);
    }
}
