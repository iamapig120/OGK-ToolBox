using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using OGKToolBox.Core.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OGKToolBox.Infrastructure.Resources;

public sealed class UnityResourceReader(
    string? classDataPath = null,
    string? managedAssembliesPath = null) : IUnityResourceReader
{
    private const int ExpressionCacheCapacity = 24;
    private readonly object _expressionCacheLock = new();
    private readonly Dictionary<ExpressionCacheKey, Task<UnityImage?>> _expressionCache = [];
    private readonly Queue<ExpressionCacheKey> _expressionCacheOrder = [];
    private readonly string _classDataPath = classDataPath
        ?? Path.Combine(AppContext.BaseDirectory, "tools", "unity", "classdata.tpk");
    private readonly string? _managedAssembliesPath = managedAssembliesPath;

    public bool IsUnityFsBundle(string path)
        => IsUnityFsFile(path);

    private static bool IsUnityFsFile(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[7];
            using var stream = File.OpenRead(path);
            return stream.Read(header) == header.Length && header.SequenceEqual("UnityFS"u8);
        }
        catch { return false; }
    }

    public Task<UnityImage?> ReadFirstImageAsync(string bundlePath, CancellationToken cancellationToken) =>
        Task.Run(() => ReadImage(bundlePath, null, cancellationToken), cancellationToken);

    public Task<UnityImage?> ReadFirstThumbnailAsync(string bundlePath, int maxSize, CancellationToken cancellationToken) =>
        Task.Run(() => ResizeForThumbnail(ReadImage(bundlePath, null, cancellationToken), maxSize), cancellationToken);

    public Task<UnityImage?> ReadImageAsync(string bundlePath, long pathId, CancellationToken cancellationToken) =>
        Task.Run(() => ReadImage(bundlePath, pathId, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<UnityImageInfo>> ListImagesAsync(string bundlePath, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<UnityImageInfo>>(() =>
        {
            var manager = new AssetsManager();
            try
            {
                var assetsFile = OpenFirstAssetsFile(manager, bundlePath);
                var result = new List<UnityImageInfo>();
                foreach (var info in assetsFile.file.GetAssetsOfType(AssetClassID.Texture2D))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var field = manager.GetBaseField(assetsFile, info);
                    if (field.IsDummy) continue;
                    var texture = TextureFile.ReadTextureFile(field);
                    result.Add(new(info.PathId, texture.m_Name, texture.m_Width, texture.m_Height));
                }
                return result;
            }
            finally { manager.UnloadAll(true); }
        }, cancellationToken);

    public Task<IReadOnlyList<UnitySpriteInfo>> ListSpritesAsync(string bundlePath, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<UnitySpriteInfo>>(() =>
        {
            var manager = new AssetsManager();
            try
            {
                var assetsFile = OpenFirstAssetsFile(manager, bundlePath);
                var result = new List<UnitySpriteInfo>();
                foreach (var info in assetsFile.file.GetAssetsOfType(AssetClassID.Sprite))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var field = manager.GetBaseField(assetsFile, info);
                    if (field.IsDummy) continue;
                    var rect = field["m_RD"]["textureRect"];
                    result.Add(new(info.PathId, field["m_Name"].AsString,
                        (int)MathF.Round(rect["width"].AsFloat), (int)MathF.Round(rect["height"].AsFloat)));
                }
                return result;
            }
            finally { manager.UnloadAll(true); }
        }, cancellationToken);

    public Task<UnityImage?> ReadSpriteAsync(string bundlePath, long pathId, CancellationToken cancellationToken) =>
        Task.Run(() => ReadSprite(bundlePath, pathId, cancellationToken), cancellationToken);

    public Task<UnityAssetGraph> ReadAssetGraphAsync(string bundlePath, CancellationToken cancellationToken) =>
        Task.Run(() => ReadAssetGraph(bundlePath, cancellationToken), cancellationToken);

    public Task<IReadOnlyDictionary<string, UnityImage>> ReadNamedImagesFromAssetsFileAsync(
        string assetsPath,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyDictionary<string, UnityImage>>(() =>
        {
            var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, UnityImage>(StringComparer.OrdinalIgnoreCase);
            var manager = new AssetsManager();
            try
            {
                var assetsFile = manager.LoadAssetsFile(assetsPath, true);
                if (!assetsFile.file.Metadata.TypeTreeEnabled)
                {
                    if (!File.Exists(_classDataPath))
                        throw new FileNotFoundException("缺少 Unity 类型数据库 classdata.tpk。", _classDataPath);
                    manager.LoadClassPackage(_classDataPath);
                    manager.LoadClassDatabaseFromPackage(assetsFile.file.Metadata.UnityVersion);
                }
                foreach (var info in assetsFile.file.GetAssetsOfType(AssetClassID.Texture2D))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var field = manager.GetBaseField(assetsFile, info);
                    if (field.IsDummy) continue;
                    var name = field["m_Name"].AsString;
                    if (!wanted.Contains(name)) continue;
                    var image = DecodeTexture(manager, assetsFile, info);
                    if (image is not null) result[name] = image;
                    if (result.Count == wanted.Count) break;
                }
                return result;
            }
            finally { manager.UnloadAll(true); }
        }, cancellationToken);

    public async Task<UnityImage?> ReadCharacterExpressionAsync(
        string bundlePath,
        long faceSpritePathId,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(bundlePath);
        var key = new ExpressionCacheKey(fullPath.ToUpperInvariant(), File.GetLastWriteTimeUtc(fullPath).Ticks, faceSpritePathId);
        Task<UnityImage?> task;
        lock (_expressionCacheLock)
        {
            if (!_expressionCache.TryGetValue(key, out task!))
            {
                task = Task.Run(() => ReadCharacterExpression(fullPath, faceSpritePathId, CancellationToken.None));
                _expressionCache[key] = task;
                _expressionCacheOrder.Enqueue(key);
                while (_expressionCache.Count > ExpressionCacheCapacity && _expressionCacheOrder.TryDequeue(out var oldest))
                    _expressionCache.Remove(oldest);
            }
        }

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch
        {
            if (task.IsFaulted)
            {
                lock (_expressionCacheLock) _expressionCache.Remove(key);
            }
            throw;
        }
    }

    private static UnityImage? ReadImage(string bundlePath, long? pathId, CancellationToken cancellationToken)
    {
        var manager = new AssetsManager();
        try
        {
            var assetsFile = OpenFirstAssetsFile(manager, bundlePath);
            // A bundle can contain masks and cropped atlases before its actual illustration.
            // Select the largest texture from metadata first, then decode only that texture.
            // Decoding every candidate is extremely expensive for thumbnail-heavy lists.
            AssetFileInfo? bestInfo = null;
            long bestArea = -1;
            foreach (var info in assetsFile.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pathId.HasValue && info.PathId != pathId.Value) continue;
                var field = manager.GetBaseField(assetsFile, info);
                if (field.IsDummy) continue;

                var texture = TextureFile.ReadTextureFile(field);
                var area = (long)texture.m_Width * texture.m_Height;
                if (area <= bestArea) continue;
                bestArea = area;
                bestInfo = info;
                if (pathId.HasValue) break;
            }

            return bestInfo is null ? null : DecodeTexture(manager, assetsFile, bestInfo);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static UnityImage? ResizeForThumbnail(UnityImage? image, int maxSize)
    {
        if (image is null || maxSize <= 0 || (image.Width <= maxSize && image.Height <= maxSize)) return image;
        var scale = Math.Min((double)maxSize / image.Width, (double)maxSize / image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var source = Image.Load<Rgba32>(image.PngBytes);
        source.Mutate(context => context.Resize(width, height));
        using var output = new MemoryStream();
        source.SaveAsPng(output);
        return new(image.Name, width, height, output.ToArray());
    }

    private UnityAssetGraph ReadAssetGraph(string bundlePath, CancellationToken cancellationToken)
    {
        var manager = new AssetsManager();
        try
        {
            if (!string.IsNullOrWhiteSpace(_managedAssembliesPath) && Directory.Exists(_managedAssembliesPath))
                manager.MonoTempGenerator = new MonoCecilTempGenerator(_managedAssembliesPath);
            var assetsFile = OpenFirstAssetsFile(manager, bundlePath);
            EnsureClassDatabase(manager, assetsFile);
            var nodes = new List<UnityAssetNode>(assetsFile.file.AssetInfos.Count);
            var references = new List<UnityAssetReference>();
            foreach (var info in assetsFile.file.AssetInfos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var classId = info.TypeId;
                var className = Enum.GetName(typeof(AssetClassID), classId) ?? $"Class{classId}";
                var field = manager.GetBaseField(assetsFile, info);
                string? name = null;
                if (!field.IsDummy)
                {
                    var nameField = field["m_Name"];
                    if (!nameField.IsDummy) name = nameField.AsString;
                    CollectReferences(field, info.PathId, string.Empty, references);
                }
                nodes.Add(new(info.PathId, classId, className, string.IsNullOrWhiteSpace(name) ? null : name));
            }
            var externals = assetsFile.file.Metadata.Externals
                .Select((external, index) => new UnityAssetExternal(index + 1, external.PathName))
                .ToArray();
            return new(Path.GetFullPath(bundlePath), assetsFile.file.Metadata.UnityVersion,
                externals, nodes, references);
        }
        finally { manager.UnloadAll(true); }
    }

    private static void CollectReferences(
        AssetTypeValueField field,
        long sourcePathId,
        string parentPath,
        List<UnityAssetReference> references)
    {
        var name = field.TemplateField.Name;
        var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}.{name}";
        var fileId = field["m_FileID"];
        var pathId = field["m_PathID"];
        if (!fileId.IsDummy && !pathId.IsDummy)
        {
            var targetPathId = pathId.AsLong;
            if (targetPathId != 0)
                references.Add(new(sourcePathId, path, fileId.AsInt, targetPathId));
            return;
        }

        foreach (var child in field.Children)
            CollectReferences(child, sourcePathId, path, references);
    }

    private static UnityImage? ReadSprite(string bundlePath, long pathId, CancellationToken cancellationToken)
    {
        var manager = new AssetsManager();
        try
        {
            var assetsFile = OpenFirstAssetsFile(manager, bundlePath);
            var spriteInfo = assetsFile.file.GetAssetsOfType(AssetClassID.Sprite).FirstOrDefault(info => info.PathId == pathId);
            return spriteInfo is null ? null : ReadSprite(manager, assetsFile, spriteInfo);
        }
        finally { manager.UnloadAll(true); }
    }

    private static UnityImage? ReadCharacterExpression(string bundlePath, long faceSpritePathId, CancellationToken cancellationToken)
    {
        var manager = new AssetsManager();
        try
        {
            var assetsFile = OpenFirstAssetsFile(manager, bundlePath);
            var sprites = assetsFile.file.GetAssetsOfType(AssetClassID.Sprite);
            var faceInfo = sprites.FirstOrDefault(info => info.PathId == faceSpritePathId);
            if (faceInfo is null) return null;
            var faceName = manager.GetBaseField(assetsFile, faceInfo)["m_Name"].AsString;
            var preferredFaceNode = faceName.Contains("_Face_A_", StringComparison.OrdinalIgnoreCase)
                ? "PAT_EyeFace"
                : "PAT_Face";
            var baseLayout = FindUiLayerLayout(manager, assetsFile, "Base", null, null);
            var faceLayout = FindUiLayerLayout(manager, assetsFile, "PAT_Face", faceSpritePathId, preferredFaceNode);
            var baseInfo = baseLayout is null
                ? sprites.FirstOrDefault(info =>
                {
                    var name = manager.GetBaseField(assetsFile, info)["m_Name"].AsString;
                    return name.Contains("_Base_", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("_Mask", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("_Aura", StringComparison.OrdinalIgnoreCase);
                })
                : sprites.FirstOrDefault(info => info.PathId == baseLayout.SpritePathId);
            if (baseInfo is null) return ReadSprite(manager, assetsFile, faceInfo);

            cancellationToken.ThrowIfCancellationRequested();
            var baseImage = ReadSprite(manager, assetsFile, baseInfo);
            var faceImage = ReadSprite(manager, assetsFile, faceInfo);
            if (baseImage is null || faceImage is null) return faceImage;

            baseLayout ??= new("Base", true, false, baseInfo.PathId, 0, 0, baseImage.Width, baseImage.Height, 0.5f, 0.5f, 1, 1);
            faceLayout ??= new(preferredFaceNode, true, false, faceSpritePathId, 0, 0, faceImage.Width, faceImage.Height, 0.5f, 0.5f, 1, 1);
            var canvasWidth = Math.Max(1, (int)MathF.Round(baseLayout.Width * baseLayout.ScaleX));
            var canvasHeight = Math.Max(1, (int)MathF.Round(baseLayout.Height * baseLayout.ScaleY));
            var (nativeFaceWidth, nativeFaceHeight) = ReadNativeUiSize(manager, assetsFile, faceInfo);
            var faceWidth = Math.Max(1, (int)MathF.Round(
                (faceLayout.UseNativeSize ? nativeFaceWidth : faceLayout.Width) * faceLayout.ScaleX));
            var faceHeight = Math.Max(1, (int)MathF.Round(
                (faceLayout.UseNativeSize ? nativeFaceHeight : faceLayout.Height) * faceLayout.ScaleY));

            using var canvas = Image.Load<Rgba32>(baseImage.PngBytes);
            using var face = Image.Load<Rgba32>(faceImage.PngBytes);
            if (canvas.Width != canvasWidth || canvas.Height != canvasHeight)
                canvas.Mutate(context => context.Resize(canvasWidth, canvasHeight));
            if (face.Width != faceWidth || face.Height != faceHeight)
                face.Mutate(context => context.Resize(faceWidth, faceHeight));

            var baseLeft = baseLayout.X - baseLayout.PivotX * canvasWidth;
            var baseTop = baseLayout.Y + (1f - baseLayout.PivotY) * canvasHeight;
            var faceLeft = faceLayout.X - faceLayout.PivotX * faceWidth;
            var faceTop = faceLayout.Y + (1f - faceLayout.PivotY) * faceHeight;
            var x = (int)MathF.Round(faceLeft - baseLeft);
            var y = (int)MathF.Round(baseTop - faceTop);
            canvas.Mutate(context => context.DrawImage(face, new Point(x, y), 1f));
            using var output = new MemoryStream();
            canvas.SaveAsPng(output);
            return new(faceImage.Name, canvas.Width, canvas.Height, output.ToArray());
        }
        finally { manager.UnloadAll(true); }
    }

    private static UnityImage? ReadSprite(AssetsManager manager, AssetsFileInstance assetsFile, AssetFileInfo spriteInfo)
    {
        var sprite = manager.GetBaseField(assetsFile, spriteInfo);
        if (sprite.IsDummy) return null;
        var renderData = sprite["m_RD"];
        var texturePathId = renderData["texture"]["m_PathID"].AsLong;
        var textureInfo = assetsFile.file.GetAssetsOfType(AssetClassID.Texture2D)
            .FirstOrDefault(info => info.PathId == texturePathId);
        if (textureInfo is null) return null;
        var atlas = DecodeTexture(manager, assetsFile, textureInfo);
        if (atlas is null) return null;

        var rect = renderData["textureRect"];
        var x = (int)MathF.Round(rect["x"].AsFloat);
        var yFromBottom = (int)MathF.Round(rect["y"].AsFloat);
        var width = (int)MathF.Round(rect["width"].AsFloat);
        var height = (int)MathF.Round(rect["height"].AsFloat);
        using var image = Image.Load<Rgba32>(atlas.PngBytes);
        var y = image.Height - yFromBottom - height;
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > image.Width || y + height > image.Height)
            return null;
        image.Mutate(context => context.Crop(new Rectangle(x, y, width, height)));

        var spriteRect = sprite["m_Rect"];
        var logicalWidth = Math.Max(width, (int)MathF.Round(spriteRect["width"].AsFloat));
        var logicalHeight = Math.Max(height, (int)MathF.Round(spriteRect["height"].AsFloat));
        var textureOffset = renderData["textureRectOffset"];
        var logicalX = (int)MathF.Round(textureOffset["x"].AsFloat);
        var logicalY = logicalHeight - (int)MathF.Round(textureOffset["y"].AsFloat) - height;
        logicalX = Math.Clamp(logicalX, 0, Math.Max(0, logicalWidth - width));
        logicalY = Math.Clamp(logicalY, 0, Math.Max(0, logicalHeight - height));

        using var logicalImage = new Image<Rgba32>(logicalWidth, logicalHeight);
        logicalImage.Mutate(context => context.DrawImage(image, new Point(logicalX, logicalY), 1f));
        using var output = new MemoryStream();
        logicalImage.SaveAsPng(output);
        return new(sprite["m_Name"].AsString, logicalWidth, logicalHeight, output.ToArray());
    }

    private static UiLayerLayout? FindUiLayerLayout(
        AssetsManager manager,
        AssetsFileInstance assetsFile,
        string gameObjectName,
        long? requiredSpritePathId,
        string? preferredGameObjectName)
    {
        var candidates = new List<UiLayerLayout>();
        foreach (var info in assetsFile.file.GetAssetsOfType(AssetClassID.GameObject))
        {
            var gameObject = manager.GetBaseField(assetsFile, info);
            var name = gameObject["m_Name"].AsString;
            var nameMatches = requiredSpritePathId is null
                ? string.Equals(name, gameObjectName, StringComparison.OrdinalIgnoreCase)
                : name.StartsWith("PAT_", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("Face", StringComparison.OrdinalIgnoreCase);
            if (!nameMatches) continue;
            var components = gameObject["m_Component"]["Array"].Children;
            AssetTypeValueField? transform = null;
            long displayedSpritePathId = 0;
            var containsRequiredSprite = requiredSpritePathId is null;
            var useNativeSize = false;

            foreach (var componentReference in components)
            {
                var componentPathId = componentReference["component"]["m_PathID"].AsLong;
                var componentInfo = assetsFile.file.GetAssetInfo(componentPathId);
                var component = manager.GetBaseField(assetsFile, componentInfo);
                if (componentInfo.TypeId == (int)AssetClassID.RectTransform) transform = component;

                var sprite = component["m_Sprite"];
                if (!sprite.IsDummy)
                {
                    var spritePathId = sprite["m_PathID"].AsLong;
                    if (spritePathId != 0) displayedSpritePathId = spritePathId;
                    if (requiredSpritePathId == spritePathId) containsRequiredSprite = true;
                }

                var spriteList = component["_sprites"];
                if (!spriteList.IsDummy)
                {
                    var containsSprite = requiredSpritePathId is not null && spriteList["Array"].Children.Any(item =>
                        item["m_PathID"].AsLong == requiredSpritePathId.Value);
                    containsRequiredSprite |= containsSprite;
                    if (containsSprite)
                    {
                        var nativeSize = component["_setNativeSize"];
                        useNativeSize = !nativeSize.IsDummy && nativeSize.AsBool;
                    }
                }
            }

            if (transform is null || !containsRequiredSprite) continue;
            if (requiredSpritePathId is null && displayedSpritePathId == 0) continue;
            var position = transform["m_AnchoredPosition"];
            var size = transform["m_SizeDelta"];
            var pivot = transform["m_Pivot"];
            var scale = transform["m_LocalScale"];
            candidates.Add(new(
                name,
                gameObject["m_IsActive"].AsBool,
                useNativeSize,
                requiredSpritePathId ?? displayedSpritePathId,
                position["x"].AsFloat,
                position["y"].AsFloat,
                MathF.Abs(size["x"].AsFloat),
                MathF.Abs(size["y"].AsFloat),
                pivot["x"].AsFloat,
                pivot["y"].AsFloat,
                MathF.Abs(scale["x"].AsFloat),
                MathF.Abs(scale["y"].AsFloat)));
        }
        return candidates
            .OrderByDescending(candidate => string.Equals(candidate.NodeName, preferredGameObjectName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate => candidate.IsActive)
            .FirstOrDefault();
    }

    private static (float Width, float Height) ReadNativeUiSize(
        AssetsManager manager,
        AssetsFileInstance assetsFile,
        AssetFileInfo spriteInfo)
    {
        var sprite = manager.GetBaseField(assetsFile, spriteInfo);
        var rect = sprite["m_Rect"];
        var pixelsToUnits = sprite["m_PixelsToUnits"].AsFloat;
        var pixelsPerUnit = pixelsToUnits <= 0 ? 1f : pixelsToUnits / 100f;
        return (rect["width"].AsFloat / pixelsPerUnit, rect["height"].AsFloat / pixelsPerUnit);
    }

    private static UnityImage? DecodeTexture(AssetsManager manager, AssetsFileInstance assetsFile, AssetFileInfo info)
    {
        var field = manager.GetBaseField(assetsFile, info);
        if (field.IsDummy) return null;
        var texture = TextureFile.ReadTextureFile(field);
        var data = texture.FillPictureData(assetsFile);
        if (data.Length == 0) return null;
        using var output = new MemoryStream();
        return texture.DecodeTextureImage(data, output, ImageExportType.Png, 0)
            ? new(texture.m_Name, texture.m_Width, texture.m_Height, output.ToArray())
            : null;
    }

    private static AssetsFileInstance OpenFirstAssetsFile(AssetsManager manager, string bundlePath)
    {
        if (!IsUnityFsFile(bundlePath)) return manager.LoadAssetsFile(bundlePath, true);
        var bundle = manager.LoadBundleFile(bundlePath, true);
        var directory = bundle.file.BlockAndDirInfo.DirectoryInfos.FindIndex(info => info.IsSerialized);
        if (directory < 0) throw new InvalidDataException($"Bundle 中没有序列化 AssetsFile：{bundlePath}");
        return manager.LoadAssetsFileFromBundle(bundle, directory, true);
    }

    private void EnsureClassDatabase(AssetsManager manager, AssetsFileInstance assetsFile)
    {
        if (assetsFile.file.Metadata.TypeTreeEnabled) return;
        if (!File.Exists(_classDataPath))
            throw new FileNotFoundException("缺少 Unity 类型数据库 classdata.tpk。", _classDataPath);
        manager.LoadClassPackage(_classDataPath);
        manager.LoadClassDatabaseFromPackage(assetsFile.file.Metadata.UnityVersion);
    }

    private readonly record struct ExpressionCacheKey(string BundlePath, long LastWriteTicks, long FaceSpritePathId);
    private sealed record UiLayerLayout(
        string NodeName,
        bool IsActive,
        bool UseNativeSize,
        long SpritePathId,
        float X,
        float Y,
        float Width,
        float Height,
        float PivotX,
        float PivotY,
        float ScaleX,
        float ScaleY);
}
