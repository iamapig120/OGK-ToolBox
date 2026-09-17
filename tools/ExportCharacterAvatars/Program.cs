using OGKToolBox.Core.Models;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Infrastructure.Resources;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/ExportCharacterAvatars -- <game-root>");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var installation = new GameInstallation(root);
if (!Directory.Exists(installation.BaseAssetsPath))
{
    Console.Error.WriteLine("The selected directory does not contain mu3_Data/StreamingAssets/assets.");
    return 2;
}

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory)
    ?? throw new DirectoryNotFoundException("Unable to locate the OGKToolBox repository root.");
var output = Path.Combine(repositoryRoot, "src", "OGKToolBox.Electron", "public", "character-avatars");
Directory.CreateDirectory(output);

var assetRoots = new List<string> { installation.BaseAssetsPath };
if (Directory.Exists(installation.OptionPath))
{
    assetRoots.AddRange(Directory.EnumerateDirectories(installation.OptionPath, "A*", SearchOption.TopDirectoryOnly)
        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        .Select(path => Path.Combine(path, "assets"))
        .Where(Directory.Exists));
}

var bundles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var assetRoot in assetRoots)
{
    foreach (var bundle in Directory.EnumerateFiles(assetRoot, "anm_chara_*", SearchOption.AllDirectories))
    {
        var key = Path.GetFileName(bundle);
        if (!CharacterExpressionRules.TryGetModelIdFromBundle(key, out var modelId) || modelId is < 1000 or > 1016)
            continue;
        bundles[key] = bundle; // Later option packages override the same bundle key, like the game's load order.
    }
}

var reader = new UnityResourceReader(
    Path.Combine(repositoryRoot, "tools", "unity", "classdata.tpk"),
    Path.Combine(root, "mu3_Data", "Managed"));
var written = 0;
foreach (var modelId in Enumerable.Range(1000, 17))
{
    var candidates = new List<(string Bundle, UnitySpriteInfo Sprite)>();
    foreach (var bundle in bundles.Values)
    {
        if (!CharacterExpressionRules.TryGetModelIdFromBundle(Path.GetFileName(bundle), out var candidateModel)
            || candidateModel != modelId)
            continue;
        candidates.AddRange((await reader.ListSpritesAsync(bundle, CancellationToken.None))
            .Where(sprite => CharacterExpressionRules.IsFaceLayer(sprite.Name))
            .Select(sprite => (bundle, sprite)));
    }
    var firstFace = candidates.OrderBy(candidate => candidate.Sprite.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    if (firstFace == default)
    {
        Console.Error.WriteLine($"No expression face found for model {modelId}.");
        continue;
    }

    var image = await reader.ReadCharacterExpressionAsync(firstFace.Bundle, firstFace.Sprite.PathId, CancellationToken.None);
    if (image is null)
    {
        Console.Error.WriteLine($"Unable to decode the first expression for model {modelId}.");
        continue;
    }

    var baseSprite = (await reader.ListSpritesAsync(firstFace.Bundle, CancellationToken.None)).FirstOrDefault(sprite =>
        sprite.Name.Contains("_Base_", StringComparison.OrdinalIgnoreCase)
        && !sprite.Name.Contains("_Mask", StringComparison.OrdinalIgnoreCase)
        && !sprite.Name.Contains("_Aura", StringComparison.OrdinalIgnoreCase));
    Rectangle? faceBounds = null;
    if (baseSprite is not null)
    {
        var baseImage = await reader.ReadSpriteAsync(firstFace.Bundle, baseSprite.PathId, CancellationToken.None);
        if (baseImage is not null) faceBounds = FindFaceBounds(baseImage.PngBytes, image.PngBytes);
    }
    await File.WriteAllBytesAsync(Path.Combine(output, $"{modelId}.png"), CropHeadshot(image.PngBytes, faceBounds, modelId));
    written++;
}

Console.WriteLine($"Wrote {written} character avatars to {output}.");
return written == 17 ? 0 : 1;

static string? FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "OGKToolBox.slnx"))) return directory.FullName;
    return null;
}

static Rectangle? FindFaceBounds(byte[] basePngBytes, byte[] expressionPngBytes)
{
    using var baseline = Image.Load<Rgba32>(basePngBytes);
    using var expression = Image.Load<Rgba32>(expressionPngBytes);
    if (baseline.Size != expression.Size) return null;
    var left = expression.Width;
    var top = expression.Height;
    var right = -1;
    var bottom = -1;
    expression.ProcessPixelRows(baseline, (expressionRows, baseRows) =>
    {
        for (var y = 0; y < expressionRows.Height; y++)
        {
            var expressionRow = expressionRows.GetRowSpan(y);
            var baseRow = baseRows.GetRowSpan(y);
            for (var x = 0; x < expressionRow.Length; x++)
            {
                if (expressionRow[x].Equals(baseRow[x])) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
    });
    return right < left || bottom < top ? null : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
}

static byte[] CropHeadshot(byte[] pngBytes, Rectangle? faceBounds, int modelId)
{
    using var image = Image.Load<Rgba32>(pngBytes);
    var left = image.Width;
    var top = image.Height;
    var right = -1;
    var bottom = -1;
    image.ProcessPixelRows(accessor =>
    {
        for (var y = 0; y < accessor.Height; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x].A == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
    });
    if (right < left || bottom < top) throw new InvalidDataException("The composed expression is transparent.");

    var characterWidth = right - left + 1;
    var characterHeight = bottom - top + 1;
    var side = Math.Max((int)Math.Ceiling(characterWidth * .48), (int)Math.Ceiling(characterHeight * .26));
    if (modelId == 1011) side = (int)Math.Round(side * .84);
    side = Math.Min(image.Width, Math.Min(image.Height, side));
    var centerX = faceBounds?.X + faceBounds?.Width / 2 ?? left + characterWidth / 2;
    var faceCenterY = faceBounds?.Y + faceBounds?.Height / 2;
    var headTop = faceCenterY.HasValue
        ? faceCenterY.Value - (int)Math.Round(side * .52)
        : top + (int)Math.Round(side * .02);
    var cropLeft = Math.Clamp(centerX - side / 2, 0, image.Width - side);
    var cropTop = Math.Clamp(headTop, 0, image.Height - side);

    image.Mutate(context => context.Crop(new Rectangle(cropLeft, cropTop, side, side)).Resize(512, 512));
    using var output = new MemoryStream();
    image.SaveAsPng(output);
    return output.ToArray();
}
