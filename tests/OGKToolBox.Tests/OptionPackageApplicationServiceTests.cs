using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Contracts;
using OGKToolBox.Application.Services;
using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Scanning;

namespace OGKToolBox.Tests;

public sealed class OptionPackageApplicationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListsBothRootsAndExcludesBaseDataFromTotals(bool duplicate)
    {
        using var temp = new TempDirectory();
        var installation = new GameInstallation(temp.Path);
        var config = "<DataConfig><version><major>1</major><minor>50</minor><release>0</release></version></DataConfig>";
        Directory.CreateDirectory(installation.BaseGameDataPath);
        File.WriteAllText(Path.Combine(installation.BaseGameDataPath, "DataConfig.xml"), config);
        var update = Path.Combine(installation.GameDataPath, "A002");
        Directory.CreateDirectory(update);
        File.WriteAllText(Path.Combine(update, "DataConfig.xml"), config);
        if (duplicate)
        {
            var option = Path.Combine(installation.OptionPath, "A002");
            Directory.CreateDirectory(option);
            File.WriteAllText(Path.Combine(option, "DataConfig.xml"), config);
        }
        var service = new OptionPackageApplicationService(new Registry(installation), new DataPackageResolver());

        var result = await service.InspectAsync("test", CancellationToken.None);

        Assert.True(result.Exists);
        Assert.Equal(duplicate ? 2 : 1, result.Packages.Count);
        Assert.Equal(result.Packages.Count, result.TotalFiles);
        Assert.Equal(result.Packages.Count * config.Length, result.TotalSize);
        Assert.Single(result.Packages, package => package.IsLoaded);
        Assert.Contains(installation.GameDataPath, result.DirectoryPaths);
        Assert.DoesNotContain(result.Packages, package => package.Name == "A000");
    }

    [Fact]
    public async Task EmptyGameDataHasZeroUpdateTotals()
    {
        using var temp = new TempDirectory();
        var installation = new GameInstallation(temp.Path);
        Directory.CreateDirectory(installation.BaseGameDataPath);
        var service = new OptionPackageApplicationService(new Registry(installation), new DataPackageResolver());
        var result = await service.InspectAsync("test", CancellationToken.None);
        Assert.True(result.Exists);
        Assert.Empty(result.Packages);
        Assert.Equal(0, result.TotalFiles);
    }

    private sealed class Registry(GameInstallation installation) : IInstallationRegistry
    {
        public InstallationSession Register(string rootPath) => GetRequired("test");
        public InstallationSession GetRequired(string installationId) => new("test", "test", installation);
        public bool TryGet(string installationId, out InstallationSession? session)
        {
            session = GetRequired(installationId);
            return true;
        }
    }
}
