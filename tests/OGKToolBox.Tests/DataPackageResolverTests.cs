using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Scanning;

namespace OGKToolBox.Tests;

public sealed class DataPackageResolverTests
{
    [Fact]
    public async Task DiscoversBaseThenOptionsInAscendingPackageOrder()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("mu3_Data", "StreamingAssets", "GameData", "A000");
        temp.CreateDirectory("mu3_Data", "StreamingAssets", "assets");
        temp.CreateDirectory("option", "A010");
        temp.CreateDirectory("option", "A002");
        temp.CreateDirectory("option", "AOMN");
        temp.CreateDirectory("option", "invalid");
        WriteConfig(System.IO.Path.Combine(temp.Path, "mu3_Data", "StreamingAssets", "GameData", "A000"), 1, 50, 0);
        WriteConfig(System.IO.Path.Combine(temp.Path, "option", "A010"), 1, 50, 1);
        WriteConfig(System.IO.Path.Combine(temp.Path, "option", "A002"), 1, 50, 0);
        WriteConfig(System.IO.Path.Combine(temp.Path, "option", "AOMN"), 1, 50, 9);

        var packages = await new DataPackageResolver().DiscoverAsync(new GameInstallation(temp.Path), CancellationToken.None);

        Assert.Equal(["A000", "A002", "A010", "AOMN"], packages.Select(item => item.Id));
        Assert.Equal([0, 1, 2, 3], packages.Select(item => item.LoadOrder));
        Assert.True(packages[0].IsBaseGame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoversGameDataUpdatesWithOrWithoutOptionDirectory(bool withOption)
    {
        using var temp = new TempDirectory();
        var installation = new GameInstallation(temp.Path);
        Directory.CreateDirectory(installation.BaseGameDataPath);
        WriteConfig(installation.BaseGameDataPath, 1, 50, 0);
        var update = Path.Combine(installation.GameDataPath, "A003");
        Directory.CreateDirectory(update);
        WriteConfig(update, 1, 50, 3);
        var incompatible = Path.Combine(installation.GameDataPath, "A004");
        Directory.CreateDirectory(incompatible);
        WriteConfig(incompatible, 1, 51, 0);
        Directory.CreateDirectory(Path.Combine(installation.GameDataPath, "unrelated"));
        if (withOption)
        {
            var option = Path.Combine(installation.OptionPath, "A002");
            Directory.CreateDirectory(option);
            WriteConfig(option, 1, 50, 2);
        }

        var packages = await new DataPackageResolver().DiscoverAsync(installation, CancellationToken.None);

        Assert.Equal(withOption ? ["A000", "A002", "A003"] : new[] { "A000", "A003" }, packages.Select(item => item.Id));
        Assert.Single(packages, item => item.IsBaseGame);
        Assert.Equal(update, packages.Single(item => item.Id == "A003").RootPath);
    }

    [Fact]
    public async Task DuplicateIdsPreferOptionAndAreNotLoadedTwice()
    {
        using var temp = new TempDirectory();
        var installation = new GameInstallation(temp.Path);
        Directory.CreateDirectory(installation.BaseGameDataPath);
        WriteConfig(installation.BaseGameDataPath, 1, 50, 0);
        foreach (var root in installation.UpdatePackagePaths)
        {
            var update = Path.Combine(root, "A002");
            Directory.CreateDirectory(update);
            WriteConfig(update, 1, 50, 2);
        }
        var packages = await new DataPackageResolver().DiscoverAsync(installation, CancellationToken.None);
        Assert.Equal(["A000", "A002"], packages.Select(item => item.Id));
        Assert.Equal(Path.Combine(installation.OptionPath, "A002"), packages[1].RootPath);
    }

    private static void WriteConfig(string directory, int major, int minor, int release) => File.WriteAllText(
        System.IO.Path.Combine(directory, "DataConfig.xml"),
        $"<DataConfig><version><major>{major}</major><minor>{minor}</minor><release>{release}</release></version></DataConfig>");
}
