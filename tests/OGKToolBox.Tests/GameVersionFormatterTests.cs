using OGKToolBox.Core.Models;

namespace OGKToolBox.Tests;

public sealed class GameVersionFormatterTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "A")]
    [InlineData(9, "I")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    public void ConvertsOptionReleaseToGameAlphabet(int release, string? expected)
    {
        Assert.Equal(expected, GameVersionFormatter.ReleaseToAlphabet(release));
    }

    [Fact]
    public void FormatsDisplayedVersionWithOneDecimalPlaceAndOptionSuffix()
    {
        Assert.Equal("1.5-I", new GameVersionInfo(new Version(1, 52, 0), 9, true).Display);
    }

    [Fact]
    public void UsesHighestCompatibleOptionReleaseForFallback()
    {
        var packages = new DataPackage[]
        {
            new("A000", "base", new Version(1, 50, 0), 0, true),
            new("A016", "option-16", new Version(1, 50, 1), 1, false),
            new("A032", "option-32", new Version(1, 50, 9), 2, false)
        };

        var version = GameVersionInfo.FromDataPackages(packages);

        Assert.Equal(9, version.OptionRelease);
        Assert.Equal("1.5-I", version.Display);
        Assert.False(version.IsAmDaemonDetected);
    }
}
