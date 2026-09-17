using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Indexing;

namespace OGKToolBox.Tests;

public sealed class SqliteLibraryIndexTests
{
    [Fact]
    public async Task SavesAndRestoresACompleteSnapshotForTheSameInstallation()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        var snapshot = Snapshot(installation);
        var index = new SqliteLibraryIndex(Path.Combine(temp.Path, "cache", "library.db"));
        await index.InitializeAsync(CancellationToken.None);

        await index.SaveAsync(snapshot, CancellationToken.None);
        var restored = await index.LoadAsync(installation, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.Cards, restored.Cards);
        Assert.Equal(snapshot.EffectiveMusic, restored.EffectiveMusic);
        Assert.Equal(snapshot.Resources, restored.Resources);
        Assert.Equal(snapshot.GameVersion, restored.GameVersion);
    }

    [Fact]
    public async Task TracksIcfFilesSeparatelyFromOptionAndOtherAmfsContent()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        var index = new SqliteLibraryIndex(Path.Combine(temp.Path, "cache", "library.db"));
        await index.SaveAsync(Snapshot(installation), CancellationToken.None);

        Assert.True(await index.IsSourceCurrentAsync(installation, CancellationToken.None));
        Assert.True(await index.IsAmfsCurrentAsync(installation, CancellationToken.None));

        File.WriteAllText(Path.Combine(installation.OptionPath, "option-change.txt"), "option");
        Assert.False(await index.IsSourceCurrentAsync(installation, CancellationToken.None));
        Assert.True(await index.IsAmfsCurrentAsync(installation, CancellationToken.None));

        Directory.CreateDirectory(installation.AmfsPath);
        File.WriteAllText(Path.Combine(installation.AmfsPath, "amfs-change.txt"), "amfs");
        Assert.True(await index.IsAmfsCurrentAsync(installation, CancellationToken.None));

        File.WriteAllText(Path.Combine(installation.AmfsPath, "ICF1"), "icf");
        Assert.False(await index.IsAmfsCurrentAsync(installation, CancellationToken.None));

        await index.SaveAsync(Snapshot(installation), CancellationToken.None);
        File.WriteAllText(Path.Combine(installation.AmfsPath, "ICF1"), "new");
        Assert.False(await index.IsAmfsCurrentAsync(installation, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidatesCacheWhenGameDataUpdateChanges()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        var index = new SqliteLibraryIndex(Path.Combine(temp.Path, "cache", "library.db"));
        await index.SaveAsync(Snapshot(installation), CancellationToken.None);
        Assert.True(await index.IsSourceCurrentAsync(installation, CancellationToken.None));

        var update = Path.Combine(installation.GameDataPath, "A002");
        Directory.CreateDirectory(update);
        File.WriteAllText(Path.Combine(update, "DataConfig.xml"), "update");
        Assert.False(await index.IsSourceCurrentAsync(installation, CancellationToken.None));
        await index.SaveAsync(Snapshot(installation), CancellationToken.None);
        Assert.True(await index.IsSourceCurrentAsync(installation, CancellationToken.None));

        File.WriteAllText(Path.Combine(update, "DataConfig.xml"), "updated version");
        Assert.False(await index.IsSourceCurrentAsync(installation, CancellationToken.None));
    }

    [Fact]
    public async Task DoesNotUseCacheForAnotherInstallation()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var index = new SqliteLibraryIndex(Path.Combine(first.Path, "cache", "library.db"));
        var installation = CreateInstallation(first.Path);
        await index.InitializeAsync(CancellationToken.None);
        await index.SaveAsync(Snapshot(installation), CancellationToken.None);

        Assert.Null(await index.LoadAsync(CreateInstallation(second.Path), CancellationToken.None));
    }

    [Fact]
    public async Task QueriesAllPagesWithoutAHiddenCapAndAppliesStructuredFilters()
    {
        using var temp = new TempDirectory();
        var installation = CreateInstallation(temp.Path);
        var origin = new ResourceOrigin("A000", installation.BaseGameDataPath, 0, true);
        var cards = Enumerable.Range(1, 455).Select(id => new Card(id, $"card{id}", $"Card {id:D3}",
            1000 + id % 3, $"Character {id % 3}", string.Empty, id % 2 == 0 ? "SSR" : "SR",
            id % 2 == 0 ? "FIRE" : "AQUA", null, null, null, null, origin)).ToArray();
        var seed = Snapshot(installation);
        var snapshot = new LibrarySnapshot
        {
            Installation = seed.Installation,
            Packages = seed.Packages,
            MusicVariants = seed.MusicVariants,
            EffectiveMusic = seed.EffectiveMusic,
            Cards = cards,
            Characters = seed.Characters,
            Resources = seed.Resources,
            Diagnostics = seed.Diagnostics
        };
        var index = new SqliteLibraryIndex();
        await index.SaveAsync(snapshot, CancellationToken.None);

        var collected = new List<Card>();
        for (var offset = 0; ; offset += 200)
        {
            var page = await index.QueryCardsAsync(installation, new LibraryQuery(offset, 200), CancellationToken.None);
            collected.AddRange(page.Items.Select(item => item.Value));
            Assert.Equal(455, page.Total);
            if (collected.Count >= page.Total) break;
        }
        Assert.Equal(455, collected.Count);
        Assert.Equal(455, collected.Select(card => card.Id).Distinct().Count());

        var filtered = await index.QueryCardsAsync(installation,
            new LibraryQuery(0, 200, Filters: new Dictionary<string, string[]> { ["rarity"] = ["SSR"] }),
            CancellationToken.None);
        Assert.Equal(227, filtered.Total);
        Assert.All(filtered.Items, item => Assert.Equal("SSR", item.Value.Rarity));
    }

    private static GameInstallation CreateInstallation(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "mu3_Data", "StreamingAssets", "GameData", "A000"));
        Directory.CreateDirectory(Path.Combine(root, "mu3_Data", "StreamingAssets", "assets"));
        Directory.CreateDirectory(Path.Combine(root, "option"));
        return new(root);
    }

    private static LibrarySnapshot Snapshot(GameInstallation installation)
    {
        var origin = new ResourceOrigin("A000", Path.Combine(installation.BaseGameDataPath, "Card.xml"), 0, true);
        return new()
        {
            Installation = installation,
            GameVersion = new GameVersionInfo(new Version(1, 52, 0), 9, true),
            Packages = [new("A000", installation.BaseGameDataPath, new Version(1, 50), 0, true)],
            MusicVariants = [],
            EffectiveMusic = [],
            Cards = [new(1, "card1", "Card 1", 2000, "Character", string.Empty, "SSR", "AQUA", null, null, null, null, origin)],
            Characters = [],
            Resources = [new("ui_card_000001", ResourceKind.Card, "bundle", 100, origin)],
            Diagnostics = []
        };
    }
}
