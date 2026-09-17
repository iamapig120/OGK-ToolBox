using System.Security.Cryptography;
using OGKToolBox.Core.Abstractions;

namespace OGKToolBox.Infrastructure.Resources;

public sealed class ChartRenderAssetManifestBuilder(string classDataPath) : IChartRenderAssetManifestBuilder
{
    private const string NoteSource = "mu3_Data/level10";
    private const string SharedSource = "mu3_Data/sharedassets10.assets";

    public async Task<ChartRenderAssetManifest> BuildAsync(
        string gameRoot,
        CancellationToken cancellationToken)
    {
        var dataPath = Path.Combine(gameRoot, "mu3_Data");
        var notePath = Path.Combine(dataPath, "level10");
        var sharedPath = Path.Combine(dataPath, "sharedassets10.assets");
        var managedPath = Path.Combine(dataPath, "Managed");
        foreach (var required in new[] { notePath, sharedPath, managedPath, classDataPath })
        {
            if (!File.Exists(required) && !Directory.Exists(required))
                throw new FileNotFoundException($"生成谱面渲染清单所需文件不存在：{required}", required);
        }

        var reader = new UnityResourceReader(classDataPath, managedPath);
        var graphs = new Dictionary<string, UnityAssetGraph>(StringComparer.OrdinalIgnoreCase)
        {
            [NoteSource] = await reader.ReadAssetGraphAsync(notePath, cancellationToken),
            [SharedSource] = await reader.ReadAssetGraphAsync(sharedPath, cancellationToken)
        };
        var graphPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NoteSource] = notePath,
            [SharedSource] = sharedPath
        };
        var noteGraph = graphs[NoteSource];
        var sharedGraph = graphs[SharedSource];
        var noteReferences = noteGraph.References
            .Where(reference => reference.FieldPath.Contains("._noteAssign.", StringComparison.Ordinal))
            .ToArray();
        var noteEffectReferences = noteGraph.References
            .Where(reference => reference.FieldPath.Contains("._noteEffectAssign.", StringComparison.Ordinal))
            .ToArray();
        var textureReferences = sharedGraph.References
            .Where(reference => reference.FieldPath.Contains("._textureAssign.", StringComparison.Ordinal))
            .ToArray();
        if (noteReferences.Length == 0 || noteEffectReferences.Length == 0 || textureReferences.Length == 0)
            throw new InvalidDataException("目标安装中没有找到 AssetAssign.NoteAssign 或 NotesPrimitiveManager.TextureAssign。请确认游戏版本。");

        var sharedFileId = noteGraph.Externals.FirstOrDefault(external =>
            Path.GetFileName(external.PathName).Equals("sharedassets10.assets", StringComparison.OrdinalIgnoreCase))?.FileId
            ?? throw new InvalidDataException("level10 没有指向 sharedassets10.assets 的 external 引用。");
        if (noteReferences.Concat(noteEffectReferences).Any(reference => reference.TargetFileId != sharedFileId))
            throw new InvalidDataException("NoteAssign 包含当前清单生成器尚未支持的目标文件。");

        var assignments = noteReferences.Select(reference => Assignment(
                "AssetAssign.NoteAssign", NoteSource, reference.SourcePathId,
                SharedSource, reference.TargetPathId, reference.FieldPath))
            .Concat(noteEffectReferences.Select(reference => Assignment(
                "AssetAssign.NoteEffectAssign", NoteSource, reference.SourcePathId,
                SharedSource, reference.TargetPathId, reference.FieldPath)))
            .Concat(textureReferences.Select(reference => Assignment(
                "NotesPrimitiveManager.TextureAssign", SharedSource, reference.SourcePathId,
                ResolveTargetFile(SharedSource, sharedGraph, reference.TargetFileId),
                reference.TargetPathId, reference.FieldPath)))
            .OrderBy(assignment => assignment.Group, StringComparer.Ordinal)
            .ThenBy(assignment => assignment.Name, StringComparer.Ordinal)
            .ToArray();

        var closure = new HashSet<AssetKey>();
        var pending = new Queue<AssetKey>();
        foreach (var assignment in assignments)
            Enqueue(new(assignment.TargetFile, assignment.TargetPathId));

        while (pending.TryDequeue(out var key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await EnsureGraphAsync(key.File, cancellationToken)) continue;
            var graph = graphs[key.File];
            foreach (var reference in graph.References.Where(item => item.SourcePathId == key.PathId))
            {
                if (reference.TargetPathId == 0) continue;
                Enqueue(new(
                    ResolveTargetFile(key.File, graph, reference.TargetFileId),
                    reference.TargetPathId));
            }
        }

        var assets = closure
            .Where(key => graphs.ContainsKey(key.File))
            .Select(key =>
            {
                var asset = graphs[key.File].Assets.FirstOrDefault(item => item.PathId == key.PathId);
                return asset is null ? null : new ChartRenderAssetNode(
                    key.File, asset.PathId, asset.ClassId, asset.ClassName, asset.Name);
            })
            .OfType<ChartRenderAssetNode>()
            .OrderBy(asset => asset.File, StringComparer.Ordinal)
            .ThenBy(asset => asset.PathId)
            .ToArray();
        var references = closure
            .Where(key => graphs.ContainsKey(key.File))
            .SelectMany(key => graphs[key.File].References
                .Where(reference => reference.SourcePathId == key.PathId)
                .Select(reference => new ChartRenderAssetEdge(
                    key.File, reference.SourcePathId, reference.FieldPath,
                    ResolveTargetFile(key.File, graphs[key.File], reference.TargetFileId),
                    reference.TargetPathId)))
            .OrderBy(reference => reference.SourceFile, StringComparer.Ordinal)
            .ThenBy(reference => reference.SourcePathId)
            .ThenBy(reference => reference.FieldPath, StringComparer.Ordinal)
            .ToArray();
        var sources = new List<ChartRenderAssetSource>();
        foreach (var pair in graphPaths.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            sources.Add(new(pair.Key, await HashAsync(pair.Value, cancellationToken)));

        return new(
            "ogktoolbox.chart-render-assets.v1",
            "SDDT 1.50",
            sharedGraph.UnityVersion,
            sources,
            assignments,
            assets,
            references);

        void Enqueue(AssetKey key)
        {
            if (key.PathId != 0 && closure.Add(key)) pending.Enqueue(key);
        }

        async Task<bool> EnsureGraphAsync(string manifestFile, CancellationToken token)
        {
            if (graphs.ContainsKey(manifestFile)) return true;
            if (!TryResolvePhysicalPath(manifestFile, dataPath, out var physicalPath)) return false;
            try
            {
                graphs[manifestFile] = await reader.ReadAssetGraphAsync(physicalPath, token);
                graphPaths[manifestFile] = physicalPath;
                return true;
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
                // Some Unity built-in pseudo files have no standalone SerializedFile on disk.
                // Keep their edge in the manifest while leaving the target unresolved.
                return false;
            }
        }
    }

    private static ChartRenderAssetAssignment Assignment(
        string group,
        string sourceFile,
        long sourcePathId,
        string targetFile,
        long targetPathId,
        string fieldPath) => new(
            group,
            fieldPath[(fieldPath.LastIndexOf('.') + 1)..],
            sourceFile,
            sourcePathId,
            targetFile,
            targetPathId);

    private static string ResolveTargetFile(string sourceFile, UnityAssetGraph graph, int fileId)
    {
        if (fileId == 0) return sourceFile;
        var pathName = graph.Externals.FirstOrDefault(external => external.FileId == fileId)?.PathName;
        if (string.IsNullOrWhiteSpace(pathName)) return $"external:{fileId}";
        var normalized = pathName.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("mu3_Data/", StringComparison.OrdinalIgnoreCase)) return normalized;
        return normalized.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)
            ? $"builtin:{normalized}"
            : $"mu3_Data/{normalized}";
    }

    private static bool TryResolvePhysicalPath(string manifestFile, string dataPath, out string physicalPath)
    {
        physicalPath = string.Empty;
        if (!manifestFile.StartsWith("mu3_Data/", StringComparison.OrdinalIgnoreCase)) return false;
        var relative = manifestFile["mu3_Data/".Length..].Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(dataPath, relative));
        var dataRoot = Path.GetFullPath(dataPath) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) return false;
        physicalPath = candidate;
        return true;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private readonly record struct AssetKey(string File, long PathId);
}
