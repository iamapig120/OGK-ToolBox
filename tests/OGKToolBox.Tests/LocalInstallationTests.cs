using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Charts;
using OGKToolBox.Infrastructure.Audio;
using OGKToolBox.Infrastructure.Configuration;
using OGKToolBox.Infrastructure.Indexing;
using OGKToolBox.Infrastructure.Resources;
using OGKToolBox.Infrastructure.Scanning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OGKToolBox.Tests;

public sealed class LocalInstallationTests
{
    [Fact]
    public async Task DecodesConfiguredListImagesDirectlyToPng()
    {
        var root = Environment.GetEnvironmentVariable("OGKTOOLBOX_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var assets = System.IO.Path.Combine(root, "mu3_Data", "StreamingAssets", "assets");
        var bundlePaths = new[]
        {
            System.IO.Path.Combine(assets, "ui_jacket_0000"),
            System.IO.Path.Combine(assets, "ui_jacket_0001"),
            System.IO.Path.Combine(assets, "ui_card_icon_000001")
        };
        if (bundlePaths.Any(path => !File.Exists(path))) return;

        var reader = new UnityResourceReader();
        foreach (var bundlePath in bundlePaths)
        {
            var image = await reader.ReadFirstImageAsync(bundlePath, CancellationToken.None);
            Assert.NotNull(image);
            Assert.True(image.PngBytes.Length > 100);
            Assert.Equal(0x89, image.PngBytes[0]);
            Assert.Equal((byte)'P', image.PngBytes[1]);
            Assert.Equal((byte)'N', image.PngBytes[2]);
            Assert.Equal((byte)'G', image.PngBytes[3]);
        }
    }

    [Fact]
    public async Task DecodesConfiguredFirstScreenJacketsDirectlyToPng()
    {
        var root = Environment.GetEnvironmentVariable("OGKTOOLBOX_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var jackets = new[]
        {
            ("A013", 7), ("A013", 8), ("A005", 9), ("A013", 11),
            ("A005", 17), ("AOMN", 22), ("AOMN", 24), ("AOMN", 25),
            ("AOMN", 26), ("AOMN", 27), ("A013", 28), ("AOMN", 29),
            ("AOMN", 30), ("AOMN", 31), ("A013", 32), ("A005", 33)
        };
        var bundlePaths = jackets.Select(item => System.IO.Path.Combine(
            root, "option", item.Item1, "assets", $"ui_jacket_{item.Item2:D4}")).ToArray();
        if (bundlePaths.Any(path => !File.Exists(path))) return;

        var reader = new UnityResourceReader();
        foreach (var bundlePath in bundlePaths)
        {
            var image = await reader.ReadFirstImageAsync(bundlePath, CancellationToken.None);
            Assert.NotNull(image);
            Assert.True(image.PngBytes.Length > 100);
        }
    }

    [Fact]
    public async Task InspectsConfiguredGameAndModConfigurationReadOnly()
    {
        var root = Environment.GetEnvironmentVariable("OGKTOOLBOX_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var validator = new GameInstallationValidator();
        Assert.True(validator.TryValidate(root, out var installation, out var error), error);

        var snapshot = await new GameConfigurationInspector().InspectAsync(
            installation!, TestContext.Current.CancellationToken);

        Assert.Contains(snapshot.Files, file => file.Kind == GameConfigurationFileKind.SegaTools && file.Exists);
        Assert.Contains(snapshot.Files, file => file.Kind == GameConfigurationFileKind.Mu3 && file.Exists);
        Assert.Contains(snapshot.Files, file => file.Kind == GameConfigurationFileKind.ConfigClient && file.Exists);
        Assert.Contains(snapshot.Files, file => file.Kind == GameConfigurationFileKind.ConfigCommon && file.Exists);
        Assert.Contains(snapshot.Files, file => file.Kind == GameConfigurationFileKind.ConfigServer && file.Exists);
        Assert.Contains(snapshot.Mods, mod => mod.Kind == InstalledModKind.MonoModPatch);
        Assert.DoesNotContain(snapshot.Files.SelectMany(file => file.Entries)
            .Where(entry => entry.IsSensitive), entry => entry.DisplayValue == entry.Value && entry.Value.Length > 0);
    }

    [Fact]
    public async Task ReadsConfiguredChartRenderAssignments()
    {
        var root = Environment.GetEnvironmentVariable("OGKTOOLBOX_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var dataPath = System.IO.Path.Combine(root, "mu3_Data");
        var classDataPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "unity", "classdata.tpk"));
        var reader = new UnityResourceReader(
            classDataPath,
            System.IO.Path.Combine(dataPath, "Managed"));

        var noteGraph = await reader.ReadAssetGraphAsync(
            System.IO.Path.Combine(dataPath, "level10"), CancellationToken.None);
        var textureGraph = await reader.ReadAssetGraphAsync(
            System.IO.Path.Combine(dataPath, "sharedassets10.assets"), CancellationToken.None);

        var noteAssignments = noteGraph.References
            .Where(reference => reference.FieldPath.Contains("._noteAssign.", StringComparison.Ordinal))
            .ToArray();
        var textureAssignments = textureGraph.References
            .Where(reference => reference.FieldPath.Contains("._textureAssign.", StringComparison.Ordinal))
            .ToArray();
        var effectAssignments = noteGraph.References
            .Where(reference => reference.FieldPath.Contains("._noteEffectAssign.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(51, noteAssignments.Length);
        Assert.Equal(25, effectAssignments.Length);
        // TextureAssign has 45 serialized fields; noTexture is null, so 44 produce PPtr edges.
        Assert.Equal(44, textureAssignments.Length);
        Assert.All(noteAssignments, reference => Assert.Equal(15, reference.SourcePathId));
        Assert.All(noteAssignments, reference => Assert.Equal(3, reference.TargetFileId));
        Assert.All(textureAssignments, reference => Assert.Equal(10953, reference.SourcePathId));
        Assert.All(textureAssignments, reference => Assert.Equal(0, reference.TargetFileId));
        Assert.Contains(noteGraph.Externals, external => external.FileId == 3
            && external.PathName.Equals("sharedassets10.assets", StringComparison.OrdinalIgnoreCase));

        var manifest = await new ChartRenderAssetManifestBuilder(classDataPath)
            .BuildAsync(root, CancellationToken.None);
        Assert.Equal("ogktoolbox.chart-render-assets.v1", manifest.Schema);
        Assert.Equal(120, manifest.Assignments.Count);
        Assert.True(manifest.Assets.Count > 1_000);
        Assert.True(manifest.References.Count > 2_000);
        Assert.True(manifest.Sources.Count >= 4);
        Assert.All(manifest.Sources, source => Assert.Equal(64, source.Sha256.Length));
        var scene = new ChartRenderDescriptorBuilder().Build(manifest);
        Assert.Equal("ogktoolbox.chart-render-scene.v1", scene.Schema);
        Assert.Equal(120, scene.Entries.Count);
        Assert.Contains(scene.Entries, entry => entry.Name == "tapRed" && entry.Meshes.Count > 0
            && entry.Materials.Count > 0 && entry.Textures.Count > 0);
        Assert.Contains(scene.Entries, entry => entry.Group == "NotesPrimitiveManager.TextureAssign"
            && entry.Root.ClassName == "Texture2D");
        Assert.Contains(scene.Entries, entry => entry.Group == "AssetAssign.NoteEffectAssign"
            && entry.Name == "normalPlatinumBreak" && entry.Textures.Count > 0);
    }

    [Fact]
    public async Task ScansConfiguredSddtInstallationAndDecodesAJacket()
    {
        var root = Environment.GetEnvironmentVariable("OGKTOOLBOX_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var validator = new GameInstallationValidator();
        Assert.True(validator.TryValidate(root, out var installation, out var error), error);
        using var temp = new TempDirectory();
        var chartSerializer = new OgkrSerializer();
        var index = new SqliteLibraryIndex(System.IO.Path.Combine(temp.Path, "library.db"));
        var scanner = new LibraryScanner(new DataPackageResolver(), new MusicMetadataReader(chartSerializer), index);

        var snapshot = await scanner.ScanAsync(installation!, null, CancellationToken.None);
        var cacheLoadTimer = System.Diagnostics.Stopwatch.StartNew();
        var cached = await scanner.LoadCachedAsync(installation!, CancellationToken.None);
        cacheLoadTimer.Stop();

        Assert.NotNull(cached);
        Assert.True(cacheLoadTimer.Elapsed < TimeSpan.FromSeconds(5),
            $"真实资源缓存恢复耗时：{cacheLoadTimer.Elapsed}");
        Assert.Equal(snapshot.EffectiveMusic.Count, cached.EffectiveMusic.Count);
        Assert.Equal(snapshot.Cards.Count, cached.Cards.Count);
        Assert.Equal(snapshot.Resources.Count, cached.Resources.Count);
        Assert.True(snapshot.MusicVariants.Count >= 1_200, $"实际 Music.xml：{snapshot.MusicVariants.Count}");
        Assert.True(snapshot.MusicVariants.Sum(music => music.Charts.Count) >= 4_500,
            $"实际已关联 OGKR：{snapshot.MusicVariants.Sum(music => music.Charts.Count)}");
        Assert.Contains(snapshot.EffectiveMusic.SelectMany(music => music.Charts), chart => chart.TotalNotes > 0);
        var previewBuilder = new OgkrChartPreviewBuilder(chartSerializer);
        var previewTimer = System.Diagnostics.Stopwatch.StartNew();
        var chartPreviews = snapshot.EffectiveMusic.SelectMany(music => music.Charts).Where(chart => chart.Exists)
            .Take(100).Select(chart => previewBuilder.Build(chart.FilePath)).ToArray();
        previewTimer.Stop();
        Assert.All(chartPreviews, preview => Assert.True(preview.Lanes.Count > 0));
        Assert.Contains(chartPreviews, preview => preview.Notes.Count > 0);
        Assert.DoesNotContain(chartPreviews.SelectMany(preview => preview.Notes)
                .Where(note => note.Kind is ChartPreviewNoteKind.Tap or ChartPreviewNoteKind.Hold),
            note => note.LaneId < 0 || note.LaneKind is null);
        Assert.All(chartPreviews.SelectMany(preview => preview.Notes)
                .Where(note => note.Kind == ChartPreviewNoteKind.Hold),
            note => Assert.True(note.EndTick > note.Tick));
        Assert.True(previewTimer.Elapsed < TimeSpan.FromSeconds(3),
            $"100 份真实谱面预览投影耗时：{previewTimer.Elapsed}");
        Assert.True(snapshot.EffectiveMusic.Count(music => music.Audio is not null) > 900,
            $"实际有关联音频的有效乐曲：{snapshot.EffectiveMusic.Count(music => music.Audio is not null)}");
        Assert.Contains(snapshot.Characters, character => character.Images.Any(image => image.Kind == ResourceKind.CardCharacter));
        var reimu = Assert.Single(snapshot.Characters, character => character.Id == 2000);
        Assert.Equal(0, reimu.GraphicCardId);
        Assert.Contains(reimu.Images, image => image.Key == "ui_card_chara_100094");
        var characterIdsWithCards = snapshot.Cards.Select(card => card.CharacterId).ToHashSet();
        Assert.DoesNotContain(snapshot.Characters, character => characterIdsWithCards.Contains(character.Id) && character.Images.Count == 0);
        Assert.Contains(snapshot.Cards, card => card.FullIllustration is not null);
        var jacket = snapshot.Resources.First(resource => resource.Kind == ResourceKind.Jacket && resource.Origin.IsEffective);
        var unity = new UnityResourceReader(System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "unity", "classdata.tpk")));
        var internalImages = await unity.ListImagesAsync(jacket.BundlePath, CancellationToken.None);
        Assert.NotEmpty(internalImages);
        var image = await unity.ReadImageAsync(jacket.BundlePath, internalImages[0].PathId, CancellationToken.None);
        Assert.NotNull(image);
        Assert.True(image.PngBytes.Length > 100);
        Assert.Equal(0x89, image.PngBytes[0]);
        var requestedNoteTextures = new[]
        {
            "mu3_nt_tap_00", "mu3_nt_tap_01", "mu3_nt_tap_02",
            "mu3_nt_extap_00", "mu3_nt_extap_01", "mu3_nt_extap_02",
            "mu3_nt_hold_00", "mu3_nt_hold_01", "mu3_nt_hold_02",
            "mu3_nt_flicktap_00"
        };
        var gameNoteTextures = await unity.ReadNamedImagesFromAssetsFileAsync(
            System.IO.Path.Combine(root, "mu3_Data", "resources.assets"),
            requestedNoteTextures, CancellationToken.None);
        Assert.Equal(requestedNoteTextures.Length, gameNoteTextures.Count);
        Assert.All(gameNoteTextures.Values, texture =>
        {
            Assert.True(texture.PngBytes.Length > 100);
            Assert.Equal(0x89, texture.PngBytes[0]);
        });
        var extractionPath = Environment.GetEnvironmentVariable("OGKTOOLBOX_CHART_ASSET_OUTPUT");
        if (!string.IsNullOrWhiteSpace(extractionPath))
        {
            Directory.CreateDirectory(extractionPath);
            foreach (var pair in gameNoteTextures)
                await File.WriteAllBytesAsync(System.IO.Path.Combine(extractionPath, pair.Key + ".png"),
                    pair.Value.PngBytes, TestContext.Current.CancellationToken);
        }

        var storyBundle = snapshot.Resources.First(resource => resource.Origin.IsEffective
            && resource.Key.Equals("anm_chara_00100001", StringComparison.OrdinalIgnoreCase));
        var expressions = await unity.ListSpritesAsync(storyBundle.BundlePath, CancellationToken.None);
        var face = expressions.First(sprite => sprite.Name.Contains("_Face_A_00", StringComparison.OrdinalIgnoreCase));
        var expression = await unity.ReadCharacterExpressionAsync(storyBundle.BundlePath, face.PathId, CancellationToken.None);
        Assert.NotNull(expression);
        Assert.True(expression.Width > face.Width);
        Assert.True(expression.Height > face.Height);
        Assert.Equal(0x89, expression.PngBytes[0]);
        var cachedExpressionTimer = System.Diagnostics.Stopwatch.StartNew();
        var cachedExpression = await unity.ReadCharacterExpressionAsync(storyBundle.BundlePath, face.PathId, CancellationToken.None);
        cachedExpressionTimer.Stop();
        Assert.Same(expression, cachedExpression);
        Assert.True(cachedExpressionTimer.Elapsed < TimeSpan.FromMilliseconds(250),
            $"缓存后的表情读取耗时：{cachedExpressionTimer.Elapsed}");

        var nativeSizeBundle = snapshot.Resources.First(resource => resource.Origin.IsEffective
            && resource.Key.Equals("anm_chara_00100612", StringComparison.OrdinalIgnoreCase));
        var nativeSprites = await unity.ListSpritesAsync(nativeSizeBundle.BundlePath, CancellationToken.None);
        var nativeBaseInfo = nativeSprites.First(sprite => sprite.Name.Contains("_Base_", StringComparison.OrdinalIgnoreCase)
            && !sprite.Name.Contains("_Mask", StringComparison.OrdinalIgnoreCase)
            && !sprite.Name.Contains("_Aura", StringComparison.OrdinalIgnoreCase));
        var nativeFaceInfo = nativeSprites.First(sprite => sprite.Name.Contains("_Face_E_00", StringComparison.OrdinalIgnoreCase));
        var nativeBase = await unity.ReadSpriteAsync(nativeSizeBundle.BundlePath, nativeBaseInfo.PathId, CancellationToken.None);
        var nativeExpression = await unity.ReadCharacterExpressionAsync(nativeSizeBundle.BundlePath, nativeFaceInfo.PathId, CancellationToken.None);
        Assert.NotNull(nativeBase);
        Assert.NotNull(nativeExpression);
        Assert.Equal((840, 1588), (nativeExpression.Width, nativeExpression.Height));
        using var basePixels = Image.Load<Rgba32>(nativeBase.PngBytes);
        using var expressionPixels = Image.Load<Rgba32>(nativeExpression.PngBytes);
        var changedBounds = FindChangedBounds(basePixels, expressionPixels);
        Assert.InRange(changedBounds.MinX, 295, 305);
        Assert.InRange(changedBounds.MinY, 170, 180);
        Assert.InRange(changedBounds.MaxX, 475, 485);
        Assert.InRange(changedBounds.MaxY, 320, 330);

        var executable = Environment.GetEnvironmentVariable("OGKTOOLBOX_VGMSTREAM");
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var service = new VgmstreamAudioPreviewService(executable);
            var reference = snapshot.EffectiveMusic.First(music => music.Audio is not null).Audio!;
            var wave = await service.DecodeToWaveAsync(reference, System.IO.Path.Combine(temp.Path, "audio"), CancellationToken.None);
            Assert.True(new FileInfo(wave).Length > 44);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(wave), 0, 4));
        }
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) FindChangedBounds(
        Image<Rgba32> baseline,
        Image<Rgba32> current)
    {
        var minX = current.Width;
        var minY = current.Height;
        var maxX = -1;
        var maxY = -1;
        current.ProcessPixelRows(baseline, (currentAccessor, baselineAccessor) =>
        {
            for (var y = 0; y < currentAccessor.Height; y++)
            {
                var currentRow = currentAccessor.GetRowSpan(y);
                var baselineRow = baselineAccessor.GetRowSpan(y);
                for (var x = 0; x < currentRow.Length; x++)
                {
                    if (currentRow[x].Equals(baselineRow[x])) continue;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        });
        return (minX, minY, maxX, maxY);
    }
}
