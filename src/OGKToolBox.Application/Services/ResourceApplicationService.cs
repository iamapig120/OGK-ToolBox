using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Contracts;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Application.Services;

public sealed class ResourceApplicationService(
    IInstallationRegistry installations,
    IOpaqueIdService ids,
    ILibraryApplicationService library,
    ILibraryScanner scanner,
    IUnityResourceReader unity,
    IChartPreviewBuilder charts,
    IAudioPreviewService audio,
    ILocalFileSystem files) : IResourceApplicationService
{
    private static readonly string[] EffectTextureNames =
    [
        "FX_hy_00_101", "FX_hy_00_102", "FX_hy_00_103", "FX_hy_00_114",
        "FX_hy_00_119", "FX_hy_00_144", "FX_hy_00_202", "FX_hy_00_243",
        "FX_hy_00_246", "FX_hy_00_917", "FX_hy_01_101", "FX_hy_01_103",
        "FX_hy_01_190", "FX_hy_01_203", "FX_hy_13_302", "FX_hy_13_307",
        "FX_hy_13_401", "FX_hy_13_500"
    ];

    public async Task<ThumbnailImage?> ReadThumbnailAsync(string installationId, string resourceId, int maxSize,
        CancellationToken cancellationToken)
    {
        var resource = await library.GetResourceAsync(installationId, resourceId, cancellationToken);
        var image = await unity.ReadFirstThumbnailAsync(resource.BundlePath, maxSize, cancellationToken);
        return image is null ? null : new(image.PngBytes);
    }

    public async Task<ThumbnailImage?> ReadMusicJacketAsync(string installationId, string musicId, int maxSize,
        CancellationToken cancellationToken)
    {
        var music = await library.GetMusicAsync(installationId, musicId, cancellationToken);
        if (music.Jacket is null) return null;
        var image = await unity.ReadFirstThumbnailAsync(music.Jacket.BundlePath, maxSize, cancellationToken);
        return image is null ? null : new(image.PngBytes);
    }

    public async Task<ThumbnailImage?> ReadCardImageAsync(string installationId, string cardId, string visual, int maxSize,
        CancellationToken cancellationToken)
    {
        var card = await library.GetCardAsync(installationId, cardId, cancellationToken);
        var resource = visual switch
        {
            "full" => card.FullIllustration,
            "character" => card.CharacterImage,
            "icon" => card.Icon,
            _ => card.Image
        };
        if (resource is null) return null;
        var image = await unity.ReadFirstThumbnailAsync(resource.BundlePath, maxSize, cancellationToken);
        return image is null ? null : new(image.PngBytes);
    }

    public async Task<ChartPreview> BuildChartPreviewAsync(string installationId, string chartId,
        CancellationToken cancellationToken)
    {
        var values = Require(ids.Read(chartId, "chart"), installationId, 3);
        var music = await library.GetMusicAsync(installationId,
            ids.Create("music", installationId, values[1]), cancellationToken);
        if (!int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var difficulty))
            throw new KeyNotFoundException("The selected chart is unavailable.");
        var chart = music.Charts.FirstOrDefault(item => item.Difficulty == difficulty && item.Exists)
            ?? throw new KeyNotFoundException("The selected chart is unavailable.");
        return charts.Build(chart.FilePath);
    }

    public async Task<string> DecodeMusicAsync(string installationId, string musicId,
        CancellationToken cancellationToken)
    {
        var session = installations.GetRequired(installationId);
        var music = await library.GetMusicAsync(installationId, musicId, cancellationToken);
        if (music.Audio is null) throw new KeyNotFoundException("The selected music has no playable audio.");
        return await audio.DecodeToWaveAsync(music.Audio, session.Installation.AudioCachePath, cancellationToken);
    }

    public async Task<IReadOnlyList<UnityImage>> ReadChartEffectTexturesAsync(string installationId,
        CancellationToken cancellationToken)
    {
        var session = installations.GetRequired(installationId);
        var path = Path.Combine(session.Installation.RootPath, "mu3_Data", "sharedassets10.assets");
        if (!files.FileExists(path)) return [];
        var images = await unity.ReadNamedImagesFromAssetsFileAsync(path, EffectTextureNames, cancellationToken);
        return images.Values.OrderBy(image => image.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<ExpressionInfo>> ListExpressionsAsync(string installationId, int modelId,
        CancellationToken cancellationToken)
    {
        if (modelId <= 0) return [];
        var prefix = $"anm_chara_{modelId:D6}";
        var result = new List<ExpressionInfo>();
        var filters = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = [((int)ResourceKind.Character).ToString(CultureInfo.InvariantCulture)],
            ["effective"] = ["1"]
        };
        var resources = new List<GameResource>();
        for (var offset = 0; ; offset += 200)
        {
            var page = await library.QueryResourcesAsync(installationId,
                new LibraryQuery(offset, 200, prefix, Filters: filters), cancellationToken);
            resources.AddRange(page.Items.Select(item => item.Value));
            if (resources.Count >= page.Total || page.Items.Count == 0) break;
        }
        foreach (var resource in resources.Where(resource =>
                     resource.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var logical = resource.Key + "\u001f" + resource.Origin.PackageId;
            var sprites = await unity.ListSpritesAsync(resource.BundlePath, cancellationToken);
            result.AddRange(sprites.Where(sprite => CharacterExpressionRules.IsFaceLayer(sprite.Name))
                .Select(sprite => new ExpressionInfo(
                    ids.Create("expression", installationId, logical,
                        sprite.PathId.ToString(CultureInfo.InvariantCulture)), sprite.Name, resource.Key)));
        }
        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<UnityImage?> ReadExpressionAsync(string installationId, string expressionId,
        CancellationToken cancellationToken)
    {
        var values = Require(ids.Read(expressionId, "expression"), installationId, 3);
        if (!long.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pathId) || pathId == 0)
            throw new KeyNotFoundException("The selected expression is unavailable.");
        var resource = await library.GetResourceAsync(installationId,
            ids.Create("resource", installationId, values[1]), cancellationToken);
        return await unity.ReadCharacterExpressionAsync(resource.BundlePath, pathId, cancellationToken);
    }

    public async Task<BinaryContent> ExportAsync(string installationId, string kind, string itemId,
        CancellationToken cancellationToken)
    {
        return kind switch
        {
            "resource-original" => await ExportResourceAsync(installationId, itemId, false, cancellationToken),
            "resource-image" => await ExportResourceAsync(installationId, itemId, true, cancellationToken),
            "resource-graph" => await ExportGraphAsync(installationId, itemId, cancellationToken),
            "music-csv" => await ExportMusicCsvAsync(installationId, cancellationToken),
            "index-json" => await ExportIndexAsync(installationId, cancellationToken),
            _ => throw new ArgumentException("The requested export kind is unsupported.", nameof(kind))
        };
    }

    private async Task<BinaryContent> ExportResourceAsync(string installationId, string resourceId, bool image,
        CancellationToken cancellationToken)
    {
        var resource = await library.GetResourceAsync(installationId, resourceId, cancellationToken);
        if (!image)
            return new(Path.GetFileName(resource.BundlePath), "application/octet-stream",
                files.OpenRead(resource.BundlePath));
        var decoded = await unity.ReadFirstImageAsync(resource.BundlePath, cancellationToken)
            ?? throw new InvalidDataException("The selected resource contains no decodable image.");
        return new(SafeFileName(resource.Key) + ".png", "image/png", new MemoryStream(decoded.PngBytes, false));
    }

    private async Task<BinaryContent> ExportGraphAsync(string installationId, string resourceId,
        CancellationToken cancellationToken)
    {
        var resource = await library.GetResourceAsync(installationId, resourceId, cancellationToken);
        var graph = await unity.ReadAssetGraphAsync(resource.BundlePath, cancellationToken);
        return Json(SafeFileName(resource.Key) + "-graph.json", new
        {
            sourceFile = Path.GetFileName(graph.SourceFile),
            graph.UnityVersion,
            externals = graph.Externals.Select(item => new { item.FileId, PathName = Path.GetFileName(item.PathName) }),
            graph.Assets,
            graph.References
        });
    }

    private async Task<BinaryContent> ExportMusicCsvAsync(string installationId, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 4096, true))
        {
            await writer.WriteLineAsync("Id,Title,Artist,Genre,Version,Package,ChartCount,HasJacket,HasAudio".AsMemory(), cancellationToken);
            var offset = 0;
            while (true)
            {
                var page = await library.QueryMusicAsync(installationId, new LibraryQuery(offset, 200), cancellationToken);
                foreach (var indexed in page.Items)
                {
                    var item = indexed.Value;
                    var values = new[] { item.Id.ToString(CultureInfo.InvariantCulture), item.Title, item.Artist,
                        item.Genre, item.VersionName, item.Origin.PackageId,
                        item.Charts.Count.ToString(CultureInfo.InvariantCulture),
                        (item.Jacket is not null).ToString(), (item.Audio is not null).ToString() };
                    await writer.WriteLineAsync(string.Join(',', values.Select(Csv)).AsMemory(), cancellationToken);
                }
                offset += page.Items.Count;
                if (offset >= page.Total || page.Items.Count == 0) break;
            }
        }
        stream.Position = 0;
        return new("ogk-music.csv", "text/csv; charset=utf-8", stream);
    }

    private async Task<BinaryContent> ExportIndexAsync(string installationId, CancellationToken cancellationToken)
    {
        var session = installations.GetRequired(installationId);
        var snapshot = await scanner.LoadCachedAsync(session.Installation, cancellationToken)
            ?? throw new InvalidOperationException("Scan the game installation before exporting its index.");
        var export = new
        {
            schema = "ogk-library-index/v1",
            generatedAt = DateTimeOffset.UtcNow,
            packages = snapshot.Packages.Select(item => new
            {
                item.Id, version = item.Version.ToString(), item.LoadOrder, item.IsBaseGame
            }),
            music = snapshot.EffectiveMusic.Select(item => new
            {
                item.Id, item.DataName, item.Title, item.Artist, item.Genre, item.VersionName,
                package = item.Origin.PackageId,
                charts = item.Charts.Select(chart => new
                {
                    chart.Difficulty, chart.DifficultyName, chart.LevelConstant, chart.Creator,
                    chart.MainBpm, chart.TotalNotes, chart.Exists
                }),
                hasJacket = item.Jacket is not null,
                hasAudio = item.Audio is not null
            }),
            cards = snapshot.Cards.Select(item => new
            {
                item.Id, item.DataName, item.Name, item.CharacterId, item.CharacterName, item.Rarity,
                item.Attribute, package = item.Origin.PackageId
            }),
            characters = snapshot.Characters.Select(item => new
            {
                item.Id, item.DataName, item.Name, item.ModelId, package = item.Origin.PackageId
            }),
            resources = snapshot.Resources.Select(item => new
            {
                item.Key, item.Kind, item.Size, package = item.Origin.PackageId,
                item.Origin.LoadOrder, item.Origin.IsEffective
            }),
            diagnostics = snapshot.Diagnostics.Select(item => new
            {
                item.Severity, item.Code, item.Message,
                source = item.SourcePath is null ? null : Path.GetFileName(item.SourcePath)
            })
        };
        return Json("ogk-library-index.json", export);
    }

    private static BinaryContent Json(string fileName, object value)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return new(fileName, "application/json", new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value, options), false));
    }

    private static IReadOnlyList<string> Require(IReadOnlyList<string> values, string installationId, int count)
    {
        if (values.Count != count || values[0] != installationId)
            throw new KeyNotFoundException("The requested item does not belong to this installation.");
        return values;
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string SafeFileName(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
