using PackingProof.QqBot;

namespace PackingProof.QqBot.Tests;

public sealed class QqBotConfigurationTests
{
    [Fact]
    public void ValidateDeliverySettings_UsesDefaultSourceCodecProfile()
    {
        QqBotConfiguration configuration = new QqBotConfiguration().ValidateDeliverySettings();

        Assert.Equal(190, configuration.DeliveryMaxSizeMb);
        Assert.Equal(QqBotConfiguration.SourceCodecTargetSizeProfile, configuration.DeliveryProfile);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void ValidateDeliverySettings_RejectsOutOfRangeSize(int size)
    {
        var configuration = new QqBotConfiguration { DeliveryMaxSizeMb = size };

        Assert.Throws<InvalidDataException>(() => configuration.ValidateDeliverySettings());
    }

    [Fact]
    public void ValidateDeliverySettings_AcceptsH265Profile()
    {
        var configuration = new QqBotConfiguration { DeliveryProfile = " H265_TARGET_SIZE " };

        Assert.Equal(QqBotConfiguration.H265TargetSizeProfile, configuration.ValidateDeliverySettings().DeliveryProfile);
    }
}
