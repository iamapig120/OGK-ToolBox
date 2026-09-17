using Microsoft.Data.Sqlite;
using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Charts;
using OGKToolBox.Infrastructure.Indexing;
using OGKToolBox.Infrastructure.Scanning;

namespace OGKToolBox.Tests;

public sealed class LibraryScannerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoresUnityMetadataInFullAndOptionScans(bool incremental)
    {
        using var temp = new TempDirectory();
        var installation = new GameInstallation(temp.Path);
        Directory.CreateDirectory(installation.BaseGameDataPath);
        var index = new SqliteLibraryIndex(Path.Combine(temp.Path, "cache.db"));
        var scanner = new LibraryScanner(new DataPackageResolver(), new MusicMetadataReader(new OgkrSerializer()), index);
        var cached = await scanner.ScanAsync(installation, null, CancellationToken.None);
        foreach (var root in new[] { installation.BaseAssetsPath, Path.Combine(installation.OptionPath, "A001", "assets") })
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "anm_chara_00100001"), "UnityFS");
            File.WriteAllText(Path.Combine(root, "ui_jacket_000001"), "UnityFS");
            File.WriteAllText(Path.Combine(root, "anm_chara_00100001.meta"), "fileFormatVersion: 2");
            File.WriteAllText(Path.Combine(root, "ui_jacket_000001.META"), "UnityFS");
            File.WriteAllText(Path.Combine(root, "unknown.meta"), "UnityFS");
        }
        // Full scan seeds the base cache; Option refresh must still ignore sidecars.
        cached = await scanner.ScanAsync(installation, null, CancellationToken.None);
        var snapshot = incremental
            ? await scanner.ScanOptionAsync(installation, cached, null, CancellationToken.None)
            : cached;
        Assert.Equal(4, snapshot.Resources.Count);
        Assert.DoesNotContain(snapshot.Resources, item => item.BundlePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Code == "BUNDLE_HEADER_INVALID");
        Assert.Equal(2, snapshot.Resources.Count(item => item.Origin.IsEffective));
        Assert.All(snapshot.Resources.Where(item => item.Origin.IsEffective), item => Assert.Equal("A001", item.Origin.PackageId));
    }

    [Fact]
    public async Task RebuildsOldIndexesInsteadOfRestoringMisclassifiedMetadata()
    {
        using var temp = new TempDirectory();
        var installation = new GameInstallation(temp.Path);
        Directory.CreateDirectory(installation.BaseGameDataPath);
        var database = Path.Combine(temp.Path, "cache.db");
        var index = new SqliteLibraryIndex(database);
        var scanner = new LibraryScanner(new DataPackageResolver(), new MusicMetadataReader(new OgkrSerializer()), index);
        await scanner.ScanAsync(installation, null, CancellationToken.None);
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE library_state SET value = '4' WHERE key = 'cache_schema'";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        Assert.Null(await scanner.LoadCachedAsync(installation, CancellationToken.None));
        Assert.Null(await index.GetCountsAsync(installation, CancellationToken.None));
        Assert.False(await index.IsAmfsCurrentAsync(installation, CancellationToken.None));
        await scanner.ScanAsync(installation, null, CancellationToken.None);
        Assert.NotNull(await scanner.LoadCachedAsync(installation, CancellationToken.None));
    }
}
