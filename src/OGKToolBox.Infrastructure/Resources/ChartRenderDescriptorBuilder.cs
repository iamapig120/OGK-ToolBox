using OGKToolBox.Core.Abstractions;

namespace OGKToolBox.Infrastructure.Resources;

public sealed class ChartRenderDescriptorBuilder : IChartRenderDescriptorBuilder
{
    private static readonly HashSet<string> RenderResourceClasses =
        new(StringComparer.Ordinal)
        {
            "Mesh", "Material", "Texture2D", "Shader"
        };

    public ChartRenderSceneDescriptor Build(ChartRenderAssetManifest manifest)
    {
        var assets = manifest.Assets.ToDictionary(
            asset => new AssetKey(asset.File, asset.PathId));
        var outgoing = manifest.References
            .GroupBy(reference => new AssetKey(reference.SourceFile, reference.SourcePathId))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var entries = manifest.Assignments.Select(assignment => BuildEntry(assignment, assets, outgoing))
            .OrderBy(entry => entry.Group, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();

        return new(
            "ogktoolbox.chart-render-scene.v1",
            manifest.Target,
            manifest.UnityVersion,
            manifest.Sources,
            entries);
    }

    private static ChartRenderEntryDescriptor BuildEntry(
        ChartRenderAssetAssignment assignment,
        IReadOnlyDictionary<AssetKey, ChartRenderAssetNode> assets,
        IReadOnlyDictionary<AssetKey, ChartRenderAssetEdge[]> outgoing)
    {
        var rootKey = new AssetKey(assignment.TargetFile, assignment.TargetPathId);
        var visited = new HashSet<AssetKey>();
        var pending = new Stack<AssetKey>();
        var unresolved = new List<ChartRenderUnresolvedReference>();
        pending.Push(rootKey);
        while (pending.TryPop(out var key))
        {
            if (!visited.Add(key) || !outgoing.TryGetValue(key, out var references)) continue;
            foreach (var reference in references)
            {
                if (reference.TargetPathId == 0) continue;
                var target = new AssetKey(reference.TargetFile, reference.TargetPathId);
                if (assets.ContainsKey(target)) pending.Push(target);
                else unresolved.Add(new(
                    reference.SourceFile,
                    reference.SourcePathId,
                    reference.FieldPath,
                    reference.TargetFile,
                    reference.TargetPathId));
            }
        }

        var handles = visited.Where(assets.ContainsKey)
            .Select(key => Handle(assets[key]))
            .OrderBy(asset => asset.File, StringComparer.Ordinal)
            .ThenBy(asset => asset.PathId)
            .ToArray();
        var root = assets.TryGetValue(rootKey, out var rootNode)
            ? Handle(rootNode)
            : new ChartRenderAssetHandle(
                assignment.TargetFile, assignment.TargetPathId, 0, "Unresolved", null);

        return new(
            assignment.Group,
            assignment.Name,
            root,
            handles.Where(asset => !RenderResourceClasses.Contains(asset.ClassName)).ToArray(),
            handles.Where(asset => asset.ClassName == "Mesh").ToArray(),
            handles.Where(asset => asset.ClassName == "Material").ToArray(),
            handles.Where(asset => asset.ClassName == "Texture2D").ToArray(),
            handles.Where(asset => asset.ClassName == "Shader").ToArray(),
            unresolved.Distinct().OrderBy(reference => reference.SourceFile, StringComparer.Ordinal)
                .ThenBy(reference => reference.SourcePathId)
                .ThenBy(reference => reference.FieldPath, StringComparer.Ordinal)
                .ToArray());
    }

    private static ChartRenderAssetHandle Handle(ChartRenderAssetNode asset) => new(
        asset.File,
        asset.PathId,
        asset.ClassId,
        asset.ClassName,
        asset.Name);

    private readonly record struct AssetKey(string File, long PathId);
}
